// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.CodeInsights;
using MeisterDev.ProPR.Application.Features.CodeInsights.Ports;
using MeisterDev.ProPR.Application.Features.CodeInsights.Taxonomy;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.CodeInsights.Classification;

/// <summary>
///     Drains the type-classification backlog in bounded batches with bounded concurrency.
/// </summary>
/// <remarks>
///     <para>
///         Three bounds, each for a different failure it prevents. The batch size caps how much one sweep
///         costs. The concurrency cap keeps a burst of findings from saturating the client's model quota: the
///         review path shares that quota, and starving it to classify analytics would be the wrong trade. The
///         attempt ceiling stops a permanently unclassifiable finding from being retried on every sweep for as
///         long as it is retained.
///     </para>
///     <para>
///         The gate is asked once per client per sweep, not once per finding: it is the same answer for every
///         finding of that client, and a batch is normally dominated by a handful of clients.
///     </para>
/// </remarks>
public sealed partial class CodeInsightClassificationSweeper(
    ICodeInsightClassificationStore store,
    IFindingTypeClassifier classifier,
    ICodeInsightTaxonomyService taxonomyService,
    ICodeInsightsCollectionGate gate,
    ILogger<CodeInsightClassificationSweeper> logger,
    ICodeInsightRollupProjector? rollupProjector = null) : ICodeInsightClassificationSweeper
{
    /// <summary>Findings picked up per sweep.</summary>
    public const int DefaultBatchSize = 50;

    /// <summary>Classification calls in flight at once.</summary>
    public const int DefaultMaxConcurrency = 4;

    /// <summary>
    ///     Attempts a single finding gets before it is left unclassified for good. Three covers a transient
    ///     model outage without turning a finding the model simply cannot place into a permanent cost.
    /// </summary>
    public const int DefaultMaxAttempts = 3;

    private readonly int _batchSize = DefaultBatchSize;
    private readonly int _maxAttempts = DefaultMaxAttempts;
    private readonly int _maxConcurrency = DefaultMaxConcurrency;

    public async Task<CodeInsightClassificationSweepResult> SweepOnceAsync(CancellationToken ct = default)
    {
        var pending = await store.ListUnclassifiedAsync(this._batchSize, this._maxAttempts, ct);
        if (pending.Count == 0)
        {
            return new CodeInsightClassificationSweepResult(0, 0, 0, 0, 0);
        }

        var classified = 0;
        var failed = 0;
        var skippedByGate = 0;

        // One gate answer and one vocabulary lookup per client, reused across that client's findings.
        var gateByClient = new Dictionary<Guid, bool>();
        var vocabularyByClient = new Dictionary<Guid, CodeInsightTaxonomyDto>();

        using var concurrency = new SemaphoreSlim(this._maxConcurrency, this._maxConcurrency);
        var outcomes = new List<Task<bool?>>(pending.Count);

        foreach (var finding in pending)
        {
            if (!gateByClient.TryGetValue(finding.ClientId, out var enabled))
            {
                enabled = await gate.IsCollectionEnabledAsync(finding.ClientId, ct);
                gateByClient[finding.ClientId] = enabled;
            }

            if (!enabled)
            {
                // Left exactly as it is: no attempt is recorded, so if the client opts back in the finding is
                // still classifiable rather than having silently burned through its attempts while gated off.
                skippedByGate++;
                continue;
            }

            if (!vocabularyByClient.TryGetValue(finding.ClientId, out var vocabulary))
            {
                vocabulary = await taxonomyService.GetAssignableTaxonomyAsync(finding.ClientId, ct);
                vocabularyByClient[finding.ClientId] = vocabulary;
            }

            outcomes.Add(this.ClassifyOneAsync(finding, vocabulary, concurrency, ct));
        }

        foreach (var outcome in await Task.WhenAll(outcomes))
        {
            switch (outcome)
            {
                case true:
                    classified++;
                    break;
                case false:
                    failed++;
                    break;
                default:
                    break;
            }
        }

        // Refresh the roll-up once per affected job rather than once per finding: the projector recomputes a
        // whole job's cells, so per-finding calls would repeat the same work.
        if (rollupProjector is not null && classified > 0)
        {
            foreach (var jobId in pending.Select(finding => finding.JobId).Distinct())
            {
                await rollupProjector.ProjectJobAsync(jobId, ct);
            }
        }

        var backlogRemaining = await store.CountUnclassifiedAsync(this._maxAttempts, ct);

        LogSweepCompleted(logger, pending.Count, classified, failed, skippedByGate, backlogRemaining);

        return new CodeInsightClassificationSweepResult(
            pending.Count,
            classified,
            failed,
            skippedByGate,
            backlogRemaining);
    }

    /// <summary>
    ///     Classifies one finding. Returns true when it was classified, false when the attempt was spent
    ///     without a result, and null when the finding could not be attempted at all.
    /// </summary>
    private async Task<bool?> ClassifyOneAsync(
        CodeInsightUnclassifiedFinding finding,
        CodeInsightTaxonomyDto vocabulary,
        SemaphoreSlim concurrency,
        CancellationToken ct)
    {
        await concurrency.WaitAsync(ct);
        try
        {
            var result = await classifier.ClassifyAsync(
                new FindingClassificationRequest(
                    finding.ClientId,
                    finding.Id,
                    finding.Message,
                    finding.FilePath,
                    finding.LineNumber,
                    finding.Severity,
                    finding.OriginPassKind,
                    vocabulary),
                ct);

            if (result.Verdict is null)
            {
                if (!result.ModelWasAsked)
                {
                    // No model is bound for the purpose, so nothing was asked. Left exactly as it is, for the same
                    // reason a gated-off client's findings are: binding a model later must find the finding still
                    // classifiable rather than already written off.
                    return null;
                }

                // A model was asked and produced nothing usable. The attempt is recorded even though nothing was
                // learned, because otherwise a finding the model cannot place would be picked up on every sweep
                // for as long as it is retained.
                await store.RecordClassificationAttemptAsync(finding.Id, ct);
                return false;
            }

            var verdict = result.Verdict;

            await store.ApplyClassificationAsync(
                finding.Id,
                new CodeInsightClassification(
                    verdict.CoreSlugs,
                    verdict.CustomTagIds,
                    verdict.Level,
                    verdict.Qualifier,
                    verdict.Confidence,
                    classifier.ClassifierVersion),
                ct);

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One finding failing must not abort the batch.
            LogFindingFailed(logger, finding.Id, ex);
            try
            {
                await store.RecordClassificationAttemptAsync(finding.Id, CancellationToken.None);
            }
            catch (Exception attemptEx)
            {
                LogAttemptRecordFailed(logger, finding.Id, attemptEx);
            }

            return false;
        }
        finally
        {
            concurrency.Release();
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Code-insight classification sweep: considered {Considered}, classified {Classified}, "
                  + "failed {Failed}, skipped by gate {SkippedByGate}, backlog remaining {BacklogRemaining}")]
    private static partial void LogSweepCompleted(
        ILogger logger,
        int considered,
        int classified,
        int failed,
        int skippedByGate,
        int backlogRemaining);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Classifying finding {FindingId} failed; it will be retried.")]
    private static partial void LogFindingFailed(ILogger logger, Guid findingId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Could not record the failed classification attempt for finding {FindingId}; "
                  + "it may be retried more often than its ceiling allows.")]
    private static partial void LogAttemptRecordFailed(ILogger logger, Guid findingId, Exception ex);
}

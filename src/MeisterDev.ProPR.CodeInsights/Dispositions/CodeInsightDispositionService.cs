// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Globalization;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Events;
using Microsoft.Extensions.Logging;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Dispositions;
using MeisterDev.ProPR.CodeInsights.Ports;

namespace MeisterDev.ProPR.CodeInsights.Dispositions;

/// <summary>
///     Records what became of a finding when its thread resolves: derived from the crawl's own signals where
///     they settle it, and from a judgement of the discussion where they cannot.
/// </summary>
/// <remarks>
///     A sibling of thread-memory's consumer rather than a change to it. Thread-memory refuses to store some
///     resolutions on purpose (a close with no corroborating code change would teach a future review to
///     discard a still-valid finding) and those are exactly the cases a quality metric most needs recorded.
///     Best-effort throughout: it never throws into the crawl.
/// </remarks>
public sealed partial class CodeInsightDispositionService(
    ICodeInsightFindingStore findingStore,
    ICodeInsightDispositionStore dispositionStore,
    IDisregardedFindingClassifier classifier,
    ICodeInsightsCollectionGate gate,
    ILogger<CodeInsightDispositionService> logger,
    ICodeInsightRollupProjector? rollupProjector = null) : ICodeInsightDispositionService
{
    public async Task HandleThreadResolvedAsync(ThreadResolvedDomainEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        try
        {
            if (!await gate.IsCollectionEnabledAsync(evt.ClientId, ct))
            {
                return;
            }

            // The provider thread id captured at materialisation is the join. The crawl carries the thread id
            // as a number and the store holds the provider's own string form, so the conversion is explicit
            // and invariant: a culture-dependent one would silently never match.
            var providerThreadId = evt.ThreadId.ToString(CultureInfo.InvariantCulture);

            var finding = await findingStore.FindByProviderThreadAsync(
                evt.ClientId,
                evt.RepositoryId,
                evt.PullRequestId,
                providerThreadId,
                ct);

            if (finding is null)
            {
                // Not one of our findings: raised before collection was enabled for this client, authored by a
                // human, or on a provider whose thread ids were never captured. Skipped, never attached to a
                // finding that does not exist.
                LogNoMatchingFinding(logger, evt.ThreadId, evt.ClientId);
                return;
            }

            if (await dispositionStore.GetDispositionAsync(finding.Id, ct) is not null)
            {
                // The crawl sees the same resolved thread on every pass. Deciding again could change a number
                // a report has already shown.
                return;
            }

            var record = await this.ResolveDispositionAsync(evt, finding, ct);
            var decided = await dispositionStore.RecordDispositionAsync(finding.Id, record, ct);

            if (decided)
            {
                LogDispositionRecorded(logger, finding.Id, record.Disposition);

                // The outcome changes this job's per-outcome counts. Recomputation, so calling it again here is
                // safe, and the count lands in the review's bucket rather than today's.
                if (rollupProjector is not null)
                {
                    await rollupProjector.ProjectJobAsync(finding.JobId, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A collection side-write must never disrupt the crawl that produced the event.
            LogHandlingFailed(logger, evt.ThreadId, evt.ClientId, ex);
        }
    }

    private async Task<CodeInsightDispositionRecord> ResolveDispositionAsync(
        ThreadResolvedDomainEvent evt,
        CodeInsightFindingView finding,
        CancellationToken ct)
    {
        var fromSignals = FindingDispositionMapper.MapFromSignals(evt.Intent, evt.CodeChangedSinceRaised);
        if (fromSignals is not null)
        {
            // Determined by the signals alone, no model call, and no classifier version to record.
            return new CodeInsightDispositionRecord(
                fromSignals.Value,
                evt.Intent,
                evt.CodeChangedSinceRaised,
                ClassifierVersion: null,
                ClassifierConfidence: null);
        }

        var judgement = await classifier.JudgeAsync(
            new DisregardedFindingJudgementRequest(
                evt.ClientId,
                finding.Id,
                finding.Message,
                finding.FilePath,
                evt.CommentHistory,
                evt.ChangeExcerpt),
            ct);

        if (judgement is null)
        {
            // Unjudged, so the finding is recorded as dismissed rather than as a false positive. Calling it
            // wrong on the strength of a failed model call would charge the reviewer for a mistake nobody
            // established, and precision is the number that reads worst when inflated.
            LogSplitUndecided(logger, finding.Id);
            return new CodeInsightDispositionRecord(
                CodeInsightDisposition.Dismissed,
                evt.Intent,
                evt.CodeChangedSinceRaised,
                classifier.ClassifierVersion,
                ClassifierConfidence: null);
        }

        if (judgement.IsUnresolved)
        {
            // A human engaged and nobody decided. Recording that as a rejection would charge the reviewer for a
            // verdict nobody gave, and as an acceptance would credit it with one.
            return new CodeInsightDispositionRecord(
                CodeInsightDisposition.Discussed,
                evt.Intent,
                evt.CodeChangedSinceRaised,
                classifier.ClassifierVersion,
                judgement.Confidence);
        }

        return new CodeInsightDispositionRecord(
            judgement.WasWrong ? CodeInsightDisposition.FalsePositive : CodeInsightDisposition.Dismissed,
            evt.Intent,
            evt.CodeChangedSinceRaised,
            classifier.ClassifierVersion,
            judgement.Confidence,
            // Null where the classifier judged the split but not the reason. The outcome is still worth
            // recording, and a guessed reason in a distribution is worse than an honest gap in one.
            judgement.Reason);
    }
}

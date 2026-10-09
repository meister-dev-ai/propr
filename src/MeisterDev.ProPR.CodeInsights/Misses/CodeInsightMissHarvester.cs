// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Text;
using System.Security.Cryptography;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Events;
using Microsoft.Extensions.Logging;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Ports;

namespace MeisterDev.ProPR.CodeInsights.Misses;

/// <summary>Retains human review threads and evaluates missed findings after structural and ownership checks.</summary>
/// <remarks>Collection failures are retained when source ownership is known; cancellation propagates to the caller.</remarks>
public sealed partial class CodeInsightMissHarvester(
    MeisterDev.ProPR.Application.Interfaces.IScmProviderCompatibilityCodec compatibilityCodec,
    ICodeInsightFindingStore findingStore,
    ICodeInsightMissStore missStore,
    IHumanMissClassifier classifier,
    ICodeInsightsCollectionGate gate,
    IPostedCommentComposer postedCommentComposer,
    ILogger<CodeInsightMissHarvester> logger,
    IFindingTypeClassifier? typeClassifier = null,
    MeisterDev.ProPR.CodeInsights.Taxonomy.ICodeInsightTaxonomyService? taxonomy = null,
    MeisterDev.ProPR.CodeInsights.Rollups.ReviewerPerformanceCountProjector? performanceProjector = null) : ICodeInsightMissHarvester
{
    public async Task<bool> HandleThreadObservedAsync(ThreadUpdatedEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var observedAt = evt.ObservedAt ?? DateTimeOffset.UtcNow;
        observedAt = new DateTimeOffset(observedAt.UtcTicks / 10 * 10, TimeSpan.Zero);

        var attempt = new HarvestAttempt(evt);
        try
        {
            return await this.ObserveCoreAsync(attempt, observedAt, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogHandlingFailed(logger, evt.ThreadId, evt.ClientId, ex);
            try
            {
                return await this.RetainFailedObservationAsync(attempt, observedAt, ct);
            }
            catch (Exception recordingException) when (!ct.IsCancellationRequested)
            {
                LogHandlingFailed(logger, evt.ThreadId, evt.ClientId, recordingException);
            }

            return false;
        }
    }

    private async Task<bool> ObserveCoreAsync(HarvestAttempt attempt, DateTimeOffset observedAt, CancellationToken ct)
    {
        var evt = attempt.Event;
        // The producer captures authorship; own-thread outcomes belong to disposition collection.
        if (evt.Comments.Count == 0)
        {
            return true;
        }

        var key = new CodeInsightPullRequestKey(evt.ClientId, evt.RepositoryId, evt.PullRequestId);
        var threadResolved = IsResolved(evt.Status);
        var discussion = BuildDiscussion(evt);
        if (!evt.Comments.Any(comment => comment.IsAiAuthored) &&
            (discussion.Length == 0 || !HarvestedThreadEligibility.IsHumanThread(discussion, postedCommentComposer.Text)))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(evt.ThreadId))
        {
            return false;
        }

        if (!await gate.IsCollectionEnabledAsync(evt.ClientId, ct))
        {
            return evt.Comments.Any(comment => comment.IsAiAuthored);
        }

        evt = evt with
        {
            ProviderScope = evt.ProviderScope ?? await findingStore.ResolveProviderScopeAsync(evt.ClientId, evt.ConnectionId, ct) ?? string.Empty
        };
        attempt.Event = evt;
        var eligibility = await missStore.ObserveThreadEligibilityAsync(
            key, evt.ThreadId, evt.ProviderScope ?? string.Empty, evt.Comments.Any(comment => comment.IsAiAuthored), observedAt, ct);
        if (evt.Comments.Any(comment => comment.IsAiAuthored))
        {
            if (eligibility.SourceObservedAt > observedAt)
            {
                return await AcknowledgedAsync(key, evt, observedAt, false, ct);
            }

            var retainedSource = await missStore.GetObservationAsync(key, evt.ThreadId, ct, evt.ConnectionId, evt.ProviderScope);
            return await ExcludeOwnFindingAsync(key, evt, "", observedAt, retainedSource, ct);
        }

        var judgedThreadResolved = await missStore.GetJudgedThreadResolvedAsync(key, evt.ThreadId, ct, evt.ConnectionId, evt.ProviderScope);
        var alreadyHarvested = judgedThreadResolved is not null;
        var fingerprint = SourceFingerprint(evt, discussion, classifier.ClassifierVersion);
        var source = await missStore.GetObservationAsync(key, evt.ThreadId, ct, evt.ConnectionId, evt.ProviderScope);
        var sameSource = source?.SourceFingerprint == fingerprint;

        var findings = await findingStore.GetFindingsForPullRequestAsync(key, ct);
        attempt.OwnFindingsObserved = true;
        if (!string.IsNullOrEmpty(evt.ProviderScope))
        {
            findings = findings.Where(finding => finding.ProviderScope == evt.ProviderScope).ToList();
        }

        if (MatchesOwnFinding(evt, discussion, findings))
        {
            return await ExcludeOwnFindingAsync(key, evt, fingerprint, observedAt, source, ct);
        }

        if (eligibility.SourceObservedAt > observedAt || eligibility.ExcludedFromHumanMisses)
        {
            return await AcknowledgedAsync(key, evt, observedAt, false, ct);
        }

        if (source is not null)
        {
            var acknowledgment = await missStore.ObserveUnchangedMissAsync(
                key, evt.ThreadId, fingerprint, observedAt, ct, evt.ConnectionId,
                typeClassifier is not null && taxonomy is not null ? typeClassifier.ClassifierVersion : null, providerScope: evt.ProviderScope);
            if (acknowledgment.Retained)
            {
                await this.ProjectIfChangedAsync(key, acknowledgment.Changed, ct);
                return true;
            }

            source = await missStore.GetObservationAsync(key, evt.ThreadId, ct, evt.ConnectionId, evt.ProviderScope);
            sameSource = source?.SourceFingerprint == fingerprint;
        }
        else if (alreadyHarvested && (judgedThreadResolved!.Value || !threadResolved))
        {
            return true;
        }

        var judgement = await ResolveHumanJudgementAsync(evt, discussion, threadResolved, sameSource, source, attempt, ct);
        if (judgement is null)
        {
            // Retain the failed observation without inventing substantive, scope or acted-on judgements.
            LogUnjudged(logger, evt.ThreadId, evt.ClientId);
            var failedRecord = FailedRecord(evt, discussion, classifier.ClassifierVersion, threadResolved, fingerprint, observedAt, attempt);
            return await this.RetainAndAcknowledgeAsync(key, evt, failedRecord, alreadyHarvested, observedAt, ct);
        }

        var classification = await ClassifyDimensionsAsync(evt, discussion, judgement, ct);
        var record = JudgedRecord(evt, discussion, judgement, classification, threadResolved, fingerprint, observedAt, attempt);
        return await this.RetainAndAcknowledgeAsync(key, evt, record, alreadyHarvested, observedAt, ct);
    }

    private bool MatchesOwnFinding(ThreadUpdatedEvent evt, string discussion, IReadOnlyList<CodeInsightFindingView> findings)
    {
        // Provider identity also excludes findings posted under an incorrectly attributed author account.
        if (findings.Any(finding => string.Equals(finding.ProviderThreadId, evt.ThreadId, StringComparison.Ordinal)))
        {
            LogOwnThread(logger, evt.ThreadId, evt.ClientId);
            return true;
        }

        var overlaps = HumanFindingOverlap.DuplicatesAnyFinding(
            evt.FilePath, evt.Line, discussion,
            findings.Select(finding => new FindingOverlapCandidate(finding.FilePath, finding.LineNumber, finding.Message)).ToList());
        if (overlaps)
        {
            LogDuplicateOfFinding(logger, evt.ThreadId, evt.ClientId);
        }

        return overlaps;
    }

    private CodeInsightMissRecord JudgedRecord(
        ThreadUpdatedEvent evt, string discussion, HumanMissJudgement judgement, MissDimensionClassification classification,
        bool threadResolved, string fingerprint, DateTimeOffset observedAt, HarvestAttempt attempt)
    {
        var dimensions = classification.Verdict;
        return new(
            evt.ThreadId,
            evt.FilePath,
            evt.Line,
            discussion,
            judgement.IsSubstantive,
            judgement.WasActedOn,
            judgement.IsInScope,
            judgement.Confidence,
            classifier.ClassifierVersion,
            threadResolved,
            dimensions is null ? "" : string.Join('|', dimensions.CoreSlugs.Distinct().Order(StringComparer.Ordinal)),
            dimensions?.Qualifier,
            classification.Attempted ? typeClassifier!.ClassifierVersion : null,
            dimensions?.Confidence,
            ConnectionId: evt.ConnectionId,
            SourceFingerprint: fingerprint,
            DimensionJudgementFailed: classification.Attempted && dimensions is null,
            DimensionModelWasAsked: classification.ModelWasAsked,
            SourceObservedAt: observedAt,
            JudgementModelWasAsked: attempt.HumanModelWasAsked,
            ProviderScope: evt.ProviderScope);
    }

    private static CodeInsightMissRecord FailedRecord(
        ThreadUpdatedEvent evt, string discussion, string version, bool threadResolved,
        string fingerprint, DateTimeOffset observedAt, HarvestAttempt attempt)
    {
        return new(
            evt.ThreadId, evt.FilePath, evt.Line, discussion, false, false, false, null, version, threadResolved,
            JudgementFailed: true,
            ConnectionId: evt.ConnectionId,
            SourceFingerprint: fingerprint,
            SourceObservedAt: observedAt,
            JudgementModelWasAsked: attempt.HumanModelWasAsked,
            ProviderScope: evt.ProviderScope);
    }

    private async Task<bool> RetainAndAcknowledgeAsync(
        CodeInsightPullRequestKey key, ThreadUpdatedEvent evt, CodeInsightMissRecord record, bool alreadyHarvested,
        DateTimeOffset observedAt, CancellationToken ct)
    {
        var retained = await this.RetainAsync(key, record, alreadyHarvested, ct);
        await this.ProjectIfChangedAsync(key, retained, ct);
        if (retained && record.CountsAsMiss)
        {
            LogMissHarvested(logger, evt.ThreadId, evt.ClientId);
        }

        return await AcknowledgedAsync(key, evt, observedAt, retained, ct);
    }

    private static string SourceFingerprint(ThreadUpdatedEvent evt, string discussion, string version)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{evt.Status}\n{evt.FilePath}\n{evt.Line}\n{discussion}\n{version}")));
    }

    private async Task<HumanMissJudgement?> ResolveHumanJudgementAsync(
        ThreadUpdatedEvent evt, string discussion, bool threadResolved, bool sameSource,
        CodeInsightMissObservation? source, HarvestAttempt attempt, CancellationToken ct)
    {
        HumanMissJudgement? judgement;
        if (sameSource && source is { JudgementFailed: false })
        {
            judgement = new HumanMissJudgement(source.IsSubstantive, source.WasActedOn, source.IsInScope, source.Confidence ?? 0, "Retained human judgement");
        }
        else
        {
            var request = new HumanMissJudgementRequest(evt.ClientId, evt.ThreadId, evt.FilePath, discussion, threadResolved);
            if (classifier is IHumanMissClassifierAttemptReporter attemptReporter)
            {
                var result = await attemptReporter.JudgeWithAttemptAsync(request, ct);
                judgement = result.Judgement;
                attempt.HumanModelWasAsked = result.ModelWasAsked;
            }
            else
            {
                attempt.HumanModelWasAsked = true;
                judgement = await classifier.JudgeAsync(request, ct);
            }
        }

        return judgement;
    }

    private async Task<MissDimensionClassification> ClassifyDimensionsAsync(
        ThreadUpdatedEvent evt, string discussion, HumanMissJudgement judgement, CancellationToken ct)
    {
        FindingTypeVerdict? dimensions = null;
        var dimensionAttempted = false;
        var dimensionModelWasAsked = false;
        if (judgement.IsSubstantive && typeClassifier is not null && taxonomy is not null)
        {
            try
            {
                dimensionAttempted = true;
                var vocabulary = await taxonomy.GetAssignableTaxonomyAsync(evt.ClientId, ct);
                var result = await typeClassifier.ClassifyAsync(
                    new FindingClassificationRequest(
                        evt.ClientId,
                        Guid.NewGuid(), discussion, evt.FilePath, evt.Line, MeisterDev.ProPR.Domain.Enums.CommentSeverity.Warning,
                        null, vocabulary), ct);
                dimensions = result.Verdict;
                dimensionModelWasAsked = result.ModelWasAsked;
            }
            catch (Exception exception) when (!ct.IsCancellationRequested)
            {
                LogHandlingFailed(logger, evt.ThreadId, evt.ClientId, exception);
            }
        }

        return new(dimensions, dimensionAttempted, dimensionModelWasAsked);
    }

    private sealed record MissDimensionClassification(FindingTypeVerdict? Verdict, bool Attempted, bool ModelWasAsked);

    private async Task<bool> RetainFailedObservationAsync(HarvestAttempt attempt, DateTimeOffset observedAt, CancellationToken ct)
    {
        var evt = attempt.Event;
        if (evt.Comments.Count == 0)
        {
            return true;
        }

        if (evt.Comments.Any(comment => comment.IsAiAuthored))
        {
            return false;
        }

        if (!await gate.IsCollectionEnabledAsync(evt.ClientId, ct))
        {
            return evt.Comments.Any(comment => comment.IsAiAuthored);
        }

        var discussion = BuildDiscussion(evt);
        if (discussion.Length == 0)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(evt.ThreadId))
        {
            return false;
        }

        var key = new CodeInsightPullRequestKey(evt.ClientId, evt.RepositoryId, evt.PullRequestId);
        if (!attempt.OwnFindingsObserved)
        {
            var eligibility = await missStore.GetThreadEligibilityAsync(key, evt.ThreadId, evt.ProviderScope ?? string.Empty, ct);
            // A completed judgement cannot establish that the reviewer still has no overlapping finding.
            if (eligibility is not { ExcludedFromHumanMisses: true } || eligibility.SourceObservedAt < observedAt)
            {
                return false;
            }
        }

        var version = string.IsNullOrWhiteSpace(classifier.ClassifierVersion) ? "collection-failed" : classifier.ClassifierVersion;
        var fingerprint = SourceFingerprint(evt, discussion, version);
        var source = await missStore.GetObservationAsync(key, evt.ThreadId, ct, evt.ConnectionId, evt.ProviderScope);
        if (source is not null)
        {
            var acknowledgment = await missStore.ObserveUnchangedMissAsync(
                key, evt.ThreadId, fingerprint, observedAt, ct, evt.ConnectionId,
                typeClassifier is not null && taxonomy is not null ? typeClassifier.ClassifierVersion : null, providerScope: evt.ProviderScope);
            if (acknowledgment.Retained)
            {
                await this.ProjectIfChangedAsync(key, acknowledgment.Changed, ct);
                return true;
            }
        }

        var record = FailedRecord(evt, discussion, version, IsResolved(evt.Status), fingerprint, observedAt, attempt);
        return await this.RetainAndAcknowledgeAsync(key, evt, record, source is not null, observedAt, ct);
    }

    private async Task<bool> RetainAsync(CodeInsightPullRequestKey key, CodeInsightMissRecord record, bool alreadyHarvested, CancellationToken ct)
    {
        return alreadyHarvested ? await missStore.RejudgeMissAsync(key, record, ct) : await missStore.RecordMissAsync(key, record, ct);
    }

    private async Task ProjectIfChangedAsync(CodeInsightPullRequestKey key, bool changed, CancellationToken ct)
    {
        if (changed && performanceProjector is not null)
        {
            await performanceProjector.ProjectAsync(key, ct);
        }
    }

    private sealed class HarvestAttempt(ThreadUpdatedEvent evt)
    {
        internal ThreadUpdatedEvent Event { get; set; } = evt;
        internal bool HumanModelWasAsked { get; set; }
        internal bool OwnFindingsObserved { get; set; }
    }

    private async Task<bool> AcknowledgedAsync(
        CodeInsightPullRequestKey key, ThreadUpdatedEvent evt, DateTimeOffset observedAt, bool changed, CancellationToken ct)
    {
        if (changed)
        {
            return true;
        }

        var current = await missStore.GetObservationAsync(key, evt.ThreadId, ct, evt.ConnectionId, evt.ProviderScope);
        var eligibility = await missStore.GetThreadEligibilityAsync(key, evt.ThreadId, evt.ProviderScope ?? string.Empty, ct);
        if (eligibility is { ExcludedFromHumanMisses: true } && eligibility.SourceObservedAt >= observedAt)
        {
            return current is null || current.ExcludedAsOwnFinding && current.SourceObservedAt >= eligibility.SourceObservedAt;
        }

        return current?.SourceObservedAt >= observedAt;
    }

    private async Task<bool> ExcludeOwnFindingAsync(
        CodeInsightPullRequestKey key, ThreadUpdatedEvent evt, string fingerprint,
        DateTimeOffset observedAt, CodeInsightMissObservation? source, CancellationToken ct)
    {
        if (source is null)
        {
            return true;
        }

        var acknowledgment = await missStore.ObserveUnchangedMissAsync(
            key, evt.ThreadId, fingerprint, observedAt, ct, evt.ConnectionId,
            excludedAsOwnFinding: true, providerScope: evt.ProviderScope);
        await this.ProjectIfChangedAsync(key, acknowledgment.Changed, ct);
        return acknowledgment.Retained;
    }

    /// <summary>Joins non-system comments into uncapped author-and-text lines in provider order.</summary>
    /// <remarks>
    ///     Retained text is used to reassess human-thread eligibility. Truncation could omit the only human
    ///     comment after provider activity and exclude the thread from recall. The classifier caps its own input.
    /// </remarks>
    private static string BuildDiscussion(ThreadUpdatedEvent evt)
    {
        var builder = new StringBuilder();
        foreach (var comment in evt.Comments)
        {
            if (comment.IsSystemGenerated || string.IsNullOrWhiteSpace(comment.Text))
            {
                continue;
            }

            builder.Append(comment.AuthorIdentity).Append(": ").Append(comment.Text.Trim()).Append('\n');
        }

        return builder.ToString().TrimEnd('\n');
    }

    /// <summary>Uses the shared provider-status interpreter to identify terminal thread states.</summary>
    private bool IsResolved(string? status)
    {
        return ThreadResolutionStatusInterpreter.IsResolved(compatibilityCodec.DecodeStoredThreadResolution(status));
    }
}

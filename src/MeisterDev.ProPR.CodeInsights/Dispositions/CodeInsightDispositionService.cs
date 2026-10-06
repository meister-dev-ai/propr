// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Domain.Events;
using Microsoft.Extensions.Logging;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Dispositions;
using MeisterDev.ProPR.CodeInsights.Ports;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
    ICodeInsightRollupProjector? rollupProjector = null,
    ICodeInsightPerformanceEvidenceStore? performanceEvidence = null) : ICodeInsightDispositionService
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

            // The provider thread id captured at materialisation is the join, and both sides now hold the
            // provider's own form, so there is nothing to convert between them.
            var finding = await findingStore.FindByProviderThreadAsync(
                evt.ClientId,
                evt.RepositoryId,
                evt.PullRequestId,
                evt.ThreadId,
                ct,
                evt.ProviderScope);

            if (finding is null)
            {
                // Not one of our findings: raised before collection was enabled for this client, authored by a
                // human, or on a provider whose thread ids were never captured. Skipped, never attached to a
                // finding that does not exist.
                LogNoMatchingFinding(logger, evt.ThreadId, evt.ClientId);
                return;
            }

            var existing = await dispositionStore.GetDispositionAsync(finding.Id, ct);
            var receipt = new CodeInsightPublicationReceipt(finding.ProviderScope, finding.ProviderThreadId, finding.ProviderCommentId);
            var fingerprint = Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        JsonSerializer.Serialize(
                            new
                            {
                                evt.NativeStatus,
                                evt.CodeChangedSinceRaised,
                                evt.CommentHistory,
                                evt.ChangeExcerpt,
                                classifier.ClassifierVersion,
                            }))));
            if (existing is not null && evt.NativeStatus is null)
            {
                return;
            }

            if (existing is not null && evt.NativeStatus is not null && performanceEvidence is not null
                && await performanceEvidence.ObserveUnchangedOutcomeAsync(finding.Id, evt.NativeStatus, fingerprint, evt.ObservedAt, ct, receipt))
            {
                return;
            }

            if (existing is not null && performanceEvidence is null && (evt.NativeStatus is null || string.Equals(
                        evt.NativeStatus, finding.NativeStatus, StringComparison.OrdinalIgnoreCase)
                    && finding.OutcomeSourceFingerprint == fingerprint &&
                    (finding.CurrentClassifierVersion is null || finding.CurrentClassifierVersion == classifier.ClassifierVersion)
                    && finding.CurrentCodeChange == evt.CodeChangedSinceRaised
                    && (finding.CurrentClassifierVersion is null || finding.CurrentClassifierConfidence is not null || finding.OutcomeJudgementAttempts >= 3)))
            {
                // Unchanged source evidence with a completed or exhausted judgement costs no further model call.
                return;
            }

            var isOpenObservation = evt.NativeStatus is not null && !ThreadResolutionStatusInterpreter.IsResolved(
                ThreadResolutionStatusInterpreter.InterpretIntent(evt.NativeStatus));
            var record = isOpenObservation ? null : (await this.ResolveDispositionAsync(evt, finding, ct)) with { NativeStatus = evt.NativeStatus };
            var currentChanged = performanceEvidence is not null && evt.NativeStatus is not null
                                                                 && await performanceEvidence.RecordCurrentOutcomeAsync(
                                                                     finding.Id, evt.NativeStatus, record, evt.ObservedAt, ct, fingerprint, receipt);
            var decided = record is not null && await dispositionStore.RecordDispositionAsync(finding.Id, record, ct);
            if (currentChanged && !decided && rollupProjector is not null)
            {
                await rollupProjector.ProjectJobAsync(finding.JobId, ct);
            }

            if (decided)
            {
                LogDispositionRecorded(logger, finding.Id, record!.Disposition);

                // Projection replaces the job's cohort counts, making repeated callbacks idempotent.
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

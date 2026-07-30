// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Text;
using MeisterDev.ProPR.Application.Features.CodeInsights;
using MeisterDev.ProPR.Application.Features.CodeInsights.Misses;
using MeisterDev.ProPR.Application.Features.CodeInsights.Ports;
using MeisterDev.ProPR.Domain.Events;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.CodeInsights.Misses;

/// <summary>
///     Harvests human-authored review threads that ProPR did not raise, so recall becomes measurable.
/// </summary>
/// <remarks>
///     Order matters for cost and for correctness. The cheap structural filters run first (the gate, whether
///     the thread is human at all, whether it has already been harvested) and only what survives is judged
///     by a model. The duplicate check against ProPR's own findings runs before the judgement too: a thread
///     that restates a finding is not a miss no matter how substantive it is, and paying to judge it would be
///     paying to learn nothing.
///     Best-effort: it never throws into the crawl.
/// </remarks>
public sealed partial class CodeInsightMissHarvester(
    ICodeInsightFindingStore findingStore,
    ICodeInsightMissStore missStore,
    IHumanMissClassifier classifier,
    ICodeInsightsCollectionGate gate,
    ILogger<CodeInsightMissHarvester> logger) : ICodeInsightMissHarvester
{
    /// <summary>
    ///     Provider-neutral token for a resolved thread, matching what the SCM adapters report.
    /// </summary>
    private const string ResolvedStatus = "fixed";

    public async Task HandleThreadObservedAsync(ThreadUpdatedEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        try
        {
            if (!await gate.IsCollectionEnabledAsync(evt.ClientId, ct))
            {
                return;
            }

            // Authorship was decided once by the producer and is carried on the event; a thread the AI took
            // part in is a ProPR thread, whose outcome the disposition path records instead.
            if (evt.Comments.Count == 0 || evt.Comments.Any(comment => comment.IsAiAuthored))
            {
                return;
            }

            var key = new CodeInsightPullRequestKey(evt.ClientId, evt.RepositoryId, evt.PullRequestId);

            if (await missStore.HasHarvestedThreadAsync(key, evt.ThreadId, ct))
            {
                // The crawl re-observes the same thread on every pass; harvesting it twice would double its
                // contribution to recall.
                return;
            }

            var discussion = BuildDiscussion(evt);
            if (discussion.Length == 0 || !HarvestedThreadEligibility.IsHumanThread(discussion))
            {
                // Either nothing was said, or nothing on the thread was said by a person: the provider recording
                // its own activity, or ProPR's own summary on an installation that has no provenance for it. An
                // activity entry is not a review comment, and ProPR's summary is not a thread it failed to raise.
                LogNothingHumanSaid(logger, evt.ThreadId, evt.ClientId);
                return;
            }

            var findings = await findingStore.GetFindingsForPullRequestAsync(key, ct);

            // The thread ProPR posted a finding as is ProPR's thread, whatever account it went out under. Checked
            // by identity rather than by text, because a mis-attributed author would otherwise let the reviewer's
            // own words come back as something it failed to raise, and text overlap is a weaker instrument than a
            // provider id we recorded ourselves.
            if (findings.Any(finding => string.Equals(finding.ProviderThreadId, evt.ThreadId, StringComparison.Ordinal)))
            {
                LogOwnThread(logger, evt.ThreadId, evt.ClientId);
                return;
            }

            var duplicatesAFinding = HumanFindingOverlap.DuplicatesAnyFinding(
                evt.FilePath,
                evt.Line,
                discussion,
                findings
                    .Select(finding => new FindingOverlapCandidate(
                        finding.FilePath,
                        finding.LineNumber,
                        finding.Message))
                    .ToList());

            if (duplicatesAFinding)
            {
                // ProPR raised this too. Counting it as a miss would penalise the reviewer for a finding it
                // actually produced, and the same issue must never be both a true positive and a false negative.
                LogDuplicateOfFinding(logger, evt.ThreadId, evt.ClientId);
                return;
            }

            var judgement = await classifier.JudgeAsync(
                new HumanMissJudgementRequest(
                    evt.ClientId,
                    evt.ThreadId,
                    evt.FilePath,
                    discussion,
                    IsResolved(evt.Status)),
                ct);

            if (judgement is null)
            {
                // Nothing is recorded. A harvested miss with invented judgements would be worse than an
                // unharvested one: it would show up in recall as evidence.
                LogUnjudged(logger, evt.ThreadId, evt.ClientId);
                return;
            }

            var countsAsMiss = judgement.IsSubstantive && judgement.WasActedOn && judgement.IsInScope;

            // Recorded either way, with the three judgements kept separately: the ones that did not qualify are
            // what makes the cut-off inspectable, and re-applying a changed threshold must not need the model.
            var recorded = await missStore.RecordMissAsync(
                key,
                new CodeInsightMissRecord(
                    evt.ThreadId,
                    evt.FilePath,
                    evt.Line,
                    discussion,
                    judgement.IsSubstantive,
                    judgement.WasActedOn,
                    judgement.IsInScope,
                    countsAsMiss,
                    judgement.Confidence,
                    classifier.ClassifierVersion),
                ct);

            if (recorded && countsAsMiss)
            {
                LogMissHarvested(logger, evt.ThreadId, evt.ClientId);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogHandlingFailed(logger, evt.ThreadId, evt.ClientId, ex);
        }
    }

    /// <summary>
    ///     The human side of the thread, in order. Entries the provider wrote itself are left out: "added a
    ///     reviewer", a vote, a policy result. They arrive through the same comments API as replies, and read as
    ///     review remarks nobody made, so a thread of nothing but activity produces an empty discussion and is
    ///     dropped by the caller.
    /// </summary>
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

    private static bool IsResolved(string? status)
    {
        return string.Equals(status, ResolvedStatus, StringComparison.OrdinalIgnoreCase);
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Thread {ProviderThreadId} (client {ClientId}) carries no human comment; not a miss.")]
    private static partial void LogNothingHumanSaid(ILogger logger, string providerThreadId, Guid clientId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Thread {ProviderThreadId} (client {ClientId}) is one ProPR posted a finding as; not a miss.")]
    private static partial void LogOwnThread(ILogger logger, string providerThreadId, Guid clientId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Human thread {ProviderThreadId} (client {ClientId}) restates a finding ProPR raised; not a miss.")]
    private static partial void LogDuplicateOfFinding(ILogger logger, string providerThreadId, Guid clientId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Human thread {ProviderThreadId} (client {ClientId}) could not be judged; nothing harvested.")]
    private static partial void LogUnjudged(ILogger logger, string providerThreadId, Guid clientId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Human thread {ProviderThreadId} (client {ClientId}) harvested as a miss.")]
    private static partial void LogMissHarvested(ILogger logger, string providerThreadId, Guid clientId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Harvesting human thread {ProviderThreadId} (client {ClientId}) failed; "
                  + "the crawl continues unaffected.")]
    private static partial void LogHandlingFailed(
        ILogger logger,
        string providerThreadId,
        Guid clientId,
        Exception ex);
}

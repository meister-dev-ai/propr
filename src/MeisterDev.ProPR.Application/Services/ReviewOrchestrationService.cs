// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Globalization;
using System.Text.Json;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Features.Budgeting;
using MeisterDev.ProPR.Application.Features.Budgeting.Models;
using MeisterDev.ProPR.Application.Features.ReviewArchive;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Strategies.Ports;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.Support;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Events;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeisterDev.ProPR.CodeInsights.Contracts;

namespace MeisterDev.ProPR.Application.Services;

/// <summary>
///     Orchestrates the end-to-end process of handling a review job.
/// </summary>
public sealed partial class ReviewOrchestrationService(
    IReviewJobExecutionStore jobs,
    IPullRequestFetcher prFetcher,
    IScmProviderRegistry providerRegistry,
    IClientRegistry clientRegistry,
    IReviewPrScanWatermarkStore prScanRepository,
    IProtocolRecorder protocolRecorder,
    IReviewContextToolsFactory reviewContextToolsFactory,
    IRepositoryInstructionFetcher instructionFetcher,
    IRepositoryExclusionFetcher exclusionFetcher,
    IRepositoryInstructionEvaluator instructionEvaluator,
    IOptions<AiReviewOptions> options,
    ILogger<ReviewOrchestrationService> logger,
    IAiConnectionRepository aiConnectionRepository,
    IAiChatClientFactory aiChatClientFactory,
    IFileByFileReviewOrchestrator fileByFileReviewOrchestrator,
    IPromptOverrideService? promptOverrideService = null,
    IProviderActivationService? providerActivationService = null,
    IAiRuntimeResolver? aiRuntimeResolver = null,
    IReviewRepositoryWorkspaceManager? workspaceManager = null,
    IClientScmConnectionRepository? scmConnectionRepository = null,
    IReviewArchiveIngestionService? reviewArchiveIngestionService = null,
    IPostedCommentOriginStore? postedCommentOriginStore = null,
    ICodeInsightFindingIngestionService? codeInsightFindingIngestionService = null,
    IBudgetCapsProvider? budgetCapsProvider = null,
    IReviewSpendAccumulator? spendAccumulator = null,
    IBudgetScopeAccessor? budgetScopeAccessor = null,
    IBudgetEventPublisher? budgetEventPublisher = null,
    IPostedFindingIndex? postedFindingIndex = null,
    IReviewJobLeaseStore? leaseStore = null) : IReviewJobProcessor, IReviewResultPublisher
{
    private const string LocalWorkspacePreparedEventName = "local_workspace_prepared";
    private const string LocalWorkspaceFailedEventName = "local_workspace_failed";
    private const string LocalWorkspaceFallbackAppliedEventName = "local_workspace_fallback_applied";

    // Provider-neutral resolved-thread status token used across the SCM adapters.
    private const string ResolvedThreadStatus = "fixed";

    // Reply left on a freshly posted thread that the client's post configuration marks for auto-resolution.
    private const string AutoResolvedNote = "Auto-resolved by ProPR post configuration.";

    private readonly AiReviewOptions _opts = options.Value;

    private ReviewJobReuse? _reuse;

    /// <summary>
    ///     The shared adopt-prior-work rules, self-built from this service's own dependencies so nothing
    ///     changes for callers that construct this service directly. The dispatch preparer resolves the
    ///     same type from the container, which is what keeps a remote review adopting exactly what a
    ///     local one would.
    /// </summary>
    private ReviewJobReuse Reuse => this._reuse ??= new ReviewJobReuse(jobs, prScanRepository, logger);

    /// <summary>Processes the given review job end-to-end.</summary>
    public async Task ProcessAsync(ReviewJob job, CancellationToken ct)
    {
        if (providerActivationService is not null && !await providerActivationService.IsEnabledAsync(job.Provider, ct))
        {
            await jobs.SetFailedAsync(
                job.Id,
                "The provider family is currently disabled by system administration.",
                ct);
            return;
        }

        var reviewerContext = await this.ResolveReviewerAsync(job, ct);

        var resolvedReviewRuntime = await this.ResolveAiConnectionAsync(job, ct);
        if (resolvedReviewRuntime is null)
        {
            return;
        }

        var budgetScope = await this.TryCreateBudgetScopeAsync(job, ct);
        using var budgetScopeHandle = budgetScope is null ? null : budgetScopeAccessor!.BeginScope(budgetScope);

        ReviewPipelineResult? pipelineResult = null;

        try
        {
            pipelineResult = await this.RunReviewPipelineAsync(
                job,
                reviewerContext.ConfiguredTriggerReviewer,
                resolvedReviewRuntime.Value.ChatClient,
                resolvedReviewRuntime.Value.Capabilities,
                resolvedReviewRuntime.Value.LogicalModelName,
                ct);
        }
        catch (BudgetHardCapReachedException ex)
        {
            await this.HandleBudgetCutAsync(job, ex.Breach, ct);
            return;
        }
        catch (PartialReviewFailureException ex)
        {
            // A hard-cap trip can surface wrapped as a partial failure; treat it as budget-exceeded if the scope
            // tripped, otherwise fall back to the normal partial-failure handling.
            if (await this.TryHandleBudgetCutAsync(job, budgetScope, ct))
            {
                return;
            }

            await this.HandlePartialReviewFailureAsync(job, pipelineResult?.PullRequest, ex, ct);
            return;
        }
        catch (Exception ex)
        {
            if (await this.TryHandleBudgetCutAsync(job, budgetScope, ct))
            {
                return;
            }

            LogReviewFailed(logger, job.Id, ex);
            await jobs.SetFailedAsync(job.Id, ex.Message, ct);
            return;
        }

        // A completed run that stopped scanning at the per-increment soft cap emits a soft-cap event (the hard-cut
        // path returns above, so at most one event fires per job).
        if (budgetScope?.IncrementSoftCapBreach is { } incrementSoftCapBreach)
        {
            await this.EmitBudgetEventAsync(job, incrementSoftCapBreach, ct);
        }

        if (pipelineResult is not null)
        {
            await this.SaveScanAsync(job, ct);
        }
    }

    private async Task<BudgetScope?> TryCreateBudgetScopeAsync(ReviewJob job, CancellationToken ct)
    {
        if (budgetScopeAccessor is null || budgetCapsProvider is null || spendAccumulator is null)
        {
            return null;
        }

        var caps = await budgetCapsProvider.GetCapsAsync(job.ClientId, ct);
        if (!caps.AnyConfigured)
        {
            return null;
        }

        var baseline = await spendAccumulator.GetBaselineAsync(
            ReviewSpendSubject.For(job),
            DateOnly.FromDateTime(DateTime.UtcNow),
            ct);
        return new BudgetScope(caps, baseline);
    }

    private async Task<bool> TryHandleBudgetCutAsync(ReviewJob job, BudgetScope? budgetScope, CancellationToken ct)
    {
        if (budgetScope?.TrippedBreach is not { } breach)
        {
            return false;
        }

        await this.HandleBudgetCutAsync(job, breach, ct);
        return true;
    }

    private async Task HandleBudgetCutAsync(ReviewJob job, BudgetBreach breach, CancellationToken ct)
    {
        LogBudgetHardCapReached(logger, job.Id, breach.Scope, breach.ThresholdUsd, breach.SpentUsd);
        await jobs.SetBudgetExceededAsync(job.Id, breach.Scope, breach.CapKind, breach.ThresholdUsd, breach.SpentUsd, ct);
        await this.EmitBudgetEventAsync(job, breach, ct);
    }

    /// <summary>Publishes a budget event for a reached cap, for a downstream alerting capability. Never throws.</summary>
    private async Task EmitBudgetEventAsync(ReviewJob job, BudgetBreach breach, CancellationToken ct)
    {
        if (budgetEventPublisher is null)
        {
            return;
        }

        await budgetEventPublisher.PublishAsync(
            BudgetEventNotification.FromBreach(breach, job.ClientId, job.Id, job.PullRequestId, job.IterationId),
            ct);
    }

    private async Task<ReviewPipelineResult?> RunReviewPipelineAsync(
        ReviewJob job,
        ReviewerIdentity? reviewer,
        IChatClient overrideChatClient,
        AgentReviewRuntimeCapabilities runtimeCapabilities,
        string? defaultReviewLogicalModelName,
        CancellationToken ct)
    {
        LogReviewStarted(logger, job.Id, job.PullRequestId);

        var (isNewIteration, baselineJob, baselineIsFullCoverage, resumeJob, compareToIterationId, compareToReviewRevision) =
            await this.LoadScanStateAsync(job, ct);

        // Lightweight fetch: get branch names so the workspace can be prepared before the
        // full content fetch — avoids N GetItemAsync calls for ADO-backed reviews.
        var prRef = await this.FetchPullRequestRefAsync(job, ct);

        // Prepare workspace early using branch names; full content fetch uses it below.
        var workspacePreparation = await this.PrepareWorkspaceForFetchAsync(job, prRef, ct);
        var earlyWorkspace = workspacePreparation.Workspace;

        var pr = await this.TryFetchPullRequestWithCleanupAsync(
            job,
            compareToIterationId,
            compareToReviewRevision,
            earlyWorkspace,
            workspacePreparation,
            ct);
        if (pr is null)
        {
            return null;
        }

        var providerCapabilities = providerRegistry.GetRegisteredCapabilities(job.Provider) ?? [];

        // This is the execution-side copy of the rule intake also applies before queueing anything, and the
        // two have to agree: this one deletes the job rather than recording a skip, so a review intake let
        // through would otherwise vanish here with nothing said. A job that carries an explicit request
        // passes both. Without new commits there is nothing to review; a reply is the thread pass's business.
        if (!isNewIteration && !job.AllowUnchangedResubmission)
        {
            return await this.DisposeSkipAndFinalizeAsync(
                job,
                earlyWorkspace,
                workspacePreparation,
                () => LogSkippedNoChange(logger, job.Id, job.PullRequestId),
                ct);
        }

        await this.AddOptionalReviewerIfSupportedAsync(job, reviewer, providerCapabilities, ct);

        var (systemContext, carriedForwardPaths) = await this.BuildReviewContextAsync(
            job,
            pr,
            baselineJob,
            baselineIsFullCoverage,
            resumeJob,
            overrideChatClient,
            runtimeCapabilities,
            defaultReviewLogicalModelName,
            workspacePreparation,
            ct);

        if (systemContext is null)
        {
            return await this.DisposeSkipAndFinalizeAsync(
                job,
                earlyWorkspace,
                workspacePreparation,
                () => LogSkippedNoChange(logger, job.Id, job.PullRequestId),
                ct);
        }

        pr = await this.AttachLinkedItemsAsync(job, pr, systemContext, ct);

        if (this.IsJobStopped(job))
        {
            LogJobCancelledBeforeFileReview(logger, job.Id);
            return null;
        }

        var result = await this.DispatchFileReviewAsync(job, pr, systemContext, overrideChatClient, ct);

        if (this.IsJobStopped(job))
        {
            LogJobCancelledAfterFileReview(logger, job.Id);
            return null;
        }

        if (carriedForwardPaths.Count > 0)
        {
            result = result with { CarriedForwardFilePaths = carriedForwardPaths };
        }

        if (string.IsNullOrWhiteSpace(result.Summary) && result.Comments.Count == 0)
        {
            // Unlike the pre-dispatch skip paths, this one runs after DispatchFileReviewAsync,
            // whose finally block already disposed the review workspace. Disposing again here
            // would call DisposeAsync (and thus ReleaseLease) a second time on the same lease,
            // which is not idempotent, so leave the workspace disposal to the dispatch path.
            return await this.DisposeSkipAndFinalizeAsync(
                job,
                earlyWorkspace,
                workspacePreparation,
                () => LogSkippedEmptyReview(logger, job.Id, job.PullRequestId),
                ct,
                disposeWorkspace: false);
        }

        // Final status re-check immediately before the only step that posts to the provider. In a
        // multi-instance deployment a manual stop may land on another instance and never reach this
        // instance's cancellation token, so the persisted status is the last line of defence against
        // publishing the review of a job an administrator has stopped (or that was cancelled/superseded).
        if (this.IsJobStopped(job))
        {
            LogJobCancelledAfterFileReview(logger, job.Id);
            return null;
        }

        await this.PublishReviewResultAsync(job, pr, result, compareToIterationId, ct);

        await this.RetainIncrementDiffsAsync(job, pr, ct);

        return new ReviewPipelineResult(pr);
    }

    private async Task<PullRequest?> TryFetchPullRequestWithCleanupAsync(
        ReviewJob job,
        int? compareToIterationId,
        ReviewRevision? compareToReviewRevision,
        IReviewRepositoryWorkspace? earlyWorkspace,
        ReviewRepositoryWorkspacePreparationResult workspacePreparation,
        CancellationToken ct)
    {
        try
        {
            var pr = await this.FetchPullRequestAsync(
                job,
                compareToIterationId,
                compareToReviewRevision,
                earlyWorkspace,
                ct);
            if (pr is null)
            {
                await DisposeEarlyWorkspaceAsync(earlyWorkspace, workspacePreparation);
                return null;
            }

            return pr;
        }
        catch
        {
            await DisposeEarlyWorkspaceAsync(earlyWorkspace, workspacePreparation);
            throw;
        }
    }

    private async Task AddOptionalReviewerIfSupportedAsync(
        ReviewJob job,
        ReviewerIdentity? reviewer,
        IReadOnlyCollection<string> providerCapabilities,
        CancellationToken ct)
    {
        if (reviewer is null)
        {
            return;
        }

        if (!providerCapabilities.Any(capability => string.Equals(
                capability,
                "reviewAssignment",
                StringComparison.Ordinal)))
        {
            return;
        }

        await providerRegistry.GetReviewAssignmentService(job.Provider)
            .AddOptionalReviewerAsync(job.ClientId, job.CodeReviewReference, reviewer, ct);
    }

    private async Task<ReviewPipelineResult?> DisposeSkipAndFinalizeAsync(
        ReviewJob job,
        IReviewRepositoryWorkspace? earlyWorkspace,
        ReviewRepositoryWorkspacePreparationResult workspacePreparation,
        Action logSkip,
        CancellationToken ct,
        bool disposeWorkspace = true)
    {
        logSkip();
        if (disposeWorkspace)
        {
            await DisposeEarlyWorkspaceAsync(earlyWorkspace, workspacePreparation);
        }

        await this.SaveScanAndDeleteJobAsync(job, ct);
        return null;
    }

    private bool IsJobStopped(ReviewJob job)
    {
        return jobs.GetById(job.Id)?.Status is JobStatus.Cancelled or JobStatus.Superseded or JobStatus.Stopped or JobStatus.BudgetExceeded
            or JobStatus.BudgetHeld;
    }

    // Passive archive observer: when the producing connection opted in to diff retention, persist the
    // increment's per-file canonical unified diffs into the review-archive store. This runs after the
    // review is otherwise complete and decided. It never alters review behavior, deduplication, memory,
    // or the scope snapshot; the changed-file diffs are already in hand on the fetched pull request, so
    // no additional provider call is made. When retention is off it performs no diff-building work, and
    // when the archive consumer is absent it is a no-op.
    private async Task RetainIncrementDiffsAsync(ReviewJob job, PullRequest pr, CancellationToken ct)
    {
        if (reviewArchiveIngestionService is null || scmConnectionRepository is null)
        {
            return;
        }

        try
        {
            var connection = await this.ResolveRetentionConnectionAsync(job, ct);
            if (connection is null || !connection.StoreDiffs)
            {
                return;
            }

            var revisionKey = ReviewRevisionKeys.GetStoredKey(job.ReviewRevisionReference, job.IterationId);

            var fileDiffs = pr.ChangedFiles
                .Select(changedFile => new ReviewIncrementFileDiff(
                    changedFile.Path,
                    MapRetainedChangeType(changedFile.ChangeType),
                    changedFile.IsBinary,
                    changedFile.IsBinary ? string.Empty : changedFile.UnifiedDiff))
                .ToList();

            var evt = new ReviewIncrementCompletedEvent(
                job.ClientId,
                connection.Id,
                job.RepositoryId,
                job.PullRequestId,
                revisionKey,
                pr.Status.ToString(),
                DateTimeOffset.UtcNow,
                fileDiffs);

            await reviewArchiveIngestionService.HandleReviewIncrementDiffsAsync(evt, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(
                ex,
                "Diff retention ingestion failed for PR {PullRequestId}; continuing without archiving.",
                job.PullRequestId);
        }
    }

    // Passive provenance observer: persist a mapping from each provider comment this posting pass created back
    // to the originating job. Two readers depend on it. Thread retention stamps the job onto retained comments,
    // and authorship attribution uses it to recognise ProPR's own comments when the crawl sees them again.
    //
    // It records regardless of the thread-retention opt-in, which the second reader needs. A pull-request
    // summary is the only thread ProPR posts that carries no finding, so nothing else identifies it: without
    // provenance it comes back looking like a human thread ProPR failed to raise, and the reviewer's own
    // summary is charged against its recall. What is stored here is ProPR's own record of what it posted (ids
    // and timestamps, no comment content), which is a narrower thing than the retained threads the opt-in
    // governs.
    //
    // Strictly best-effort: wrapped so that nothing it does can disrupt or change publishing. When the store is
    // absent or no provider comment ids were captured, it records nothing.
    private async Task RecordPostedCommentOriginsAsync(
        ReviewJob job,
        ReviewCommentPostingDiagnosticsDto diagnostics,
        CancellationToken ct)
    {
        if (postedCommentOriginStore is null || diagnostics.PostedComments.Count == 0)
        {
            return;
        }

        try
        {
            var postedAt = DateTimeOffset.UtcNow;
            var entries = diagnostics.PostedComments
                .Where(comment => !string.IsNullOrWhiteSpace(comment.ProviderCommentId))
                .Select(comment => new PostedCommentOriginEntry(
                    job.ClientId,
                    job.RepositoryId,
                    job.PullRequestId,
                    comment.ProviderThreadId,
                    comment.ProviderCommentId,
                    job.Id,
                    postedAt))
                .ToList();

            if (entries.Count == 0)
            {
                return;
            }

            await postedCommentOriginStore.RecordAsync(entries, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogPostedCommentOriginRecordingFailed(logger, job.Id, ex);
        }
    }

    // The single-reply counterpart of the findings recording above. A reply ProPR posts into an existing thread
    // is as much its own comment as a finding it raised, and provenance is the only thing that still says so on
    // the crawl path, where no connection and therefore no token identity exists to fall back to.
    //
    // Same best-effort posture, for the same reason: the reply is already on the pull request by the time this
    // runs, and a bookkeeping failure must not undo it or fail the job. An adapter that reported no comment id
    // leaves the reply posted and unrecorded rather than blocking it.
    private async Task RecordPostedReplyOriginAsync(
        ReviewJob job,
        string providerThreadId,
        string? providerCommentId,
        CancellationToken ct)
    {
        if (postedCommentOriginStore is null || string.IsNullOrWhiteSpace(providerCommentId))
        {
            return;
        }

        try
        {
            await postedCommentOriginStore.RecordAsync(
                [
                    new PostedCommentOriginEntry(
                        job.ClientId,
                        job.RepositoryId,
                        job.PullRequestId,
                        providerThreadId,
                        providerCommentId,
                        job.Id,
                        DateTimeOffset.UtcNow),
                ],
                ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogPostedCommentOriginRecordingFailed(logger, job.Id, ex);
        }
    }

    /// <summary>
    ///     Indexes the findings this job actually posted, so a later increment can recognise the same concern
    ///     coming back and keep it off the pull request.
    /// </summary>
    /// <remarks>
    ///     Written here rather than inside a provider adapter for three reasons: the client, repository, pull
    ///     request and job identifiers are all in scope; it is already outside the per-thread posting failure
    ///     isolation, so an index problem cannot be mistaken for a posting problem; and it is provider-neutral.
    ///     <para>
    ///         It runs once, after publishing has finished. That ordering is load-bearing: a lookup during the
    ///         next job can only ever see earlier jobs' rows, so this index is strictly cross-increment and
    ///         never second-guesses the per-job deduplication that governs one review's own output.
    ///     </para>
    ///     <para>
    ///         The summary thread is excluded. It is not a finding, and pairing it as one would index review
    ///         prose under a thread that never carried a concern.
    ///     </para>
    /// </remarks>
    private async Task IndexPostedFindingsAsync(
        ReviewJob job,
        ReviewResult result,
        ReviewCommentPostingDiagnosticsDto diagnostics,
        IReadOnlySet<string> autoResolvedThreadIds,
        CancellationToken ct)
    {
        if (postedFindingIndex is null || diagnostics.PostedComments.Count == 0)
        {
            return;
        }

        try
        {
            var unclaimed = diagnostics.PostedComments
                .Where(posted => posted.ThreadKind == PostedReviewCommentKind.Inline)
                .ToList();
            var entries = new List<PostedFindingEntry>(result.Comments.Count);

            foreach (var comment in result.Comments)
            {
                var matchIndex = unclaimed.FindIndex(posted =>
                    string.Equals(posted.FilePath, comment.FilePath, StringComparison.Ordinal)
                    && posted.Line == comment.LineNumber);
                if (matchIndex < 0)
                {
                    continue;
                }

                var posted = unclaimed[matchIndex];
                unclaimed.RemoveAt(matchIndex);

                // Only a thread the provider named can be reported back as the duplicated one. Every provider
                // that creates a thread supplies its own identifier, so this excludes nothing but the case
                // where none was created at all.
                if (string.IsNullOrWhiteSpace(posted.ProviderThreadId))
                {
                    continue;
                }

                entries.Add(
                    new PostedFindingEntry(
                        job.ClientId,
                        job.RepositoryId,
                        job.PullRequestId,
                        posted.ProviderThreadId,
                        job.Id,
                        job.IterationId,
                        comment.FilePath,
                        comment.Severity,
                        comment.Message,
                        autoResolvedThreadIds.Contains(posted.ProviderThreadId ?? string.Empty)));
            }

            if (entries.Count == 0)
            {
                return;
            }

            await postedFindingIndex.RecordPostedFindingsAsync(entries, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogPostedFindingIndexingFailed(logger, job.Id, ex);
        }
    }

    // Passive code-insight observer: materialise the increment's findings as durable records with stable
    // identifiers, so quality analytics have something to attach tags, dispositions, and roll-ups to. This
    // runs after the review result is persisted and decided, over the UNFILTERED finding set: the
    // minimum-severity filter governs provider publication only, and a suppressed finding is still a
    // finding that was produced. It never alters review behaviour, deduplication, memory, or the scope
    // snapshot, and when the consumer is absent it is a no-op.
    private async Task CollectCodeInsightFindingsAsync(
        ReviewJob job,
        PullRequest pr,
        ReviewResult result,
        ReviewCommentPostingDiagnosticsDto diagnostics,
        CancellationToken ct)
    {
        if (codeInsightFindingIngestionService is null)
        {
            return;
        }

        try
        {
            var observedAt = DateTimeOffset.UtcNow;
            var revisionKey = ReviewRevisionKeys.GetStoredKey(job.ReviewRevisionReference, job.IterationId);

            var evt = new ReviewFindingsProducedEvent(
                job.ClientId,
                job.RepositoryId,
                job.PullRequestId,
                job.Id,
                revisionKey,
                pr.Status.ToString(),
                observedAt,
                BuildProducedFindings(result.Comments, diagnostics.PostedComments),
                pr.RepositoryName);

            await codeInsightFindingIngestionService.HandleReviewFindingsProducedAsync(evt, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(
                ex,
                "Code-insight collection failed for PR {PullRequestId}; continuing without collecting.",
                job.PullRequestId);
        }
    }

    // Pair each produced finding with the provider comment the posting pass created for it. The anchor
    // (file path + line) is the only basis both sides share, which is the same basis the posted-comment
    // provenance side-write uses. Each posted ref is consumed at most once so two findings on the same
    // anchor cannot both claim the same provider thread.
    private static IReadOnlyList<ReviewFindingProduced> BuildProducedFindings(
        IReadOnlyList<ReviewComment> comments,
        IReadOnlyList<PostedReviewCommentRef> postedComments)
    {
        // The summary thread shares the absent anchor of a pull-request-level finding and is posted first, so
        // without this filter the first fileless finding claimed the summary's ids and was attributed to a
        // thread that never carried a concern.
        var unclaimed = postedComments
            .Where(posted => posted.ThreadKind == PostedReviewCommentKind.Inline)
            .ToList();
        var produced = new List<ReviewFindingProduced>(comments.Count);

        for (var ordinal = 0; ordinal < comments.Count; ordinal++)
        {
            var comment = comments[ordinal];
            var matchIndex = unclaimed.FindIndex(posted =>
                string.Equals(posted.FilePath, comment.FilePath, StringComparison.Ordinal)
                && posted.Line == comment.LineNumber);

            PostedReviewCommentRef? match = null;
            if (matchIndex >= 0)
            {
                match = unclaimed[matchIndex];
                unclaimed.RemoveAt(matchIndex);
            }

            produced.Add(
                new ReviewFindingProduced(
                    ordinal,
                    comment.FilePath,
                    comment.LineNumber,
                    comment.Severity,
                    comment.Message,
                    comment.OriginPassKind,
                    comment.OriginPassIndex,
                    comment.OriginPassLens,
                    comment.OriginPassShadow,
                    comment.ScopeRelation,
                    comment.SourceReadGrounding,
                    match?.ProviderThreadId,
                    match?.ProviderCommentId,
                    comment.OriginModelId,
                    comment.OriginLogicalModelName,
                    comment.OriginSymbolName,
                    comment.OriginSymbolKind));
        }

        return produced;
    }

    private async Task<ClientScmConnectionDto?> ResolveRetentionConnectionAsync(ReviewJob job, CancellationToken ct)
    {
        if (scmConnectionRepository is null)
        {
            return null;
        }

        var host = job.ProviderHost;
        var connections = await scmConnectionRepository.GetByClientIdAsync(job.ClientId, ct);

        return connections
            .Where(connection => connection.IsActive
                                 && connection.ProviderFamily == host.Provider
                                 && ConnectionHostMatchesAuthority(connection.HostBaseUrl, host.HostBaseUrl))
            // Prefer the most specific host match when several connections share an authority.
            .OrderByDescending(connection => connection.HostBaseUrl.Length)
            .FirstOrDefault();
    }

    private static bool ConnectionHostMatchesAuthority(string connectionHostBaseUrl, string hostAuthority)
    {
        // The job host is normalized to an authority (scheme://host[:port]); a connection's stored host
        // base URL may carry a path (e.g. an Azure DevOps organization URL). Match on the authority.
        if (!Uri.TryCreate(connectionHostBaseUrl.Trim(), UriKind.Absolute, out var connectionUri))
        {
            return string.Equals(
                connectionHostBaseUrl.Trim().TrimEnd('/'),
                hostAuthority.Trim().TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase);
        }

        var connectionAuthority = connectionUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        return string.Equals(connectionAuthority, hostAuthority.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    private static string MapRetainedChangeType(ChangeType changeType)
    {
        return changeType switch
        {
            ChangeType.Add => "Added",
            ChangeType.Edit => "Modified",
            ChangeType.Delete => "Deleted",
            ChangeType.Rename => "Renamed",
            _ => "Unknown",
        };
    }

    private async Task SaveScanAndDeleteJobAsync(ReviewJob job, CancellationToken ct)
    {
        await this.SaveScanAsync(job, ct);
        await jobs.DeleteAsync(job.Id, ct);
    }

    /// <summary>
    ///     Publishes a result produced elsewhere, through the same publication an in-process review uses.
    ///     <para>
    ///         This exists so a runner's findings and an in-process review end on one publication rather
    ///         than two. Everything publication carries, deduplication at both layers, thread memory,
    ///         posted-comment origins, and per-thread failure isolation, is behaviour nobody should be
    ///         reimplementing for a remote executor, and a second entry point is how the two would drift.
    ///     </para>
    /// </summary>
    /// <param name="jobId">The job whose findings to publish.</param>
    /// <param name="result">The findings the executor produced.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task PublishAsync(Guid jobId, ReviewResult result, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        var job = jobs.GetById(jobId)
                  ?? throw new InvalidOperationException($"Review job {jobId} cannot be published because it no longer exists.");

        // The pull request is fetched here rather than sent by the executor. Publication needs the live
        // thread state to deduplicate against, and an executor's view of it is both stale and unverifiable.
        var pr = await this.FetchPullRequestAsync(job, null, null, null, ct)
                 ?? throw new InvalidOperationException($"Review job {jobId} cannot be published because its pull request could not be read.");

        await this.PublishReviewResultAsync(job, pr, result, null, ct);
    }

    private async Task PublishReviewResultAsync(
        ReviewJob job,
        PullRequest pr,
        ReviewResult result,
        int? compareToIterationId,
        CancellationToken ct)
    {
        Guid? protocolId = null;
        try
        {
            protocolId = await protocolRecorder.BeginAsync(job.Id, job.RetryCount + 1, "posting", ct: ct);
        }
        catch (Exception ex)
        {
            LogProtocolBeginFailed(logger, job.Id, ex);
        }

        // Publication is the one stretch that must not be interrupted: taking the job back while comments
        // are going out is how the same review gets posted twice. Marking it protects the job from reclaim
        // until publication finishes or its own, longer timeout passes.
        //
        // The mark is refused when the job is no longer Processing — stopped, superseded, or already
        // terminal — and that refusal is the answer to whether this review should be posted at all.
        // Publishing over it puts a review on the pull request for work somebody already decided against:
        // most visibly, a push supersedes this job while its comments are going out and the stale review
        // lands anyway. The status guard further down only runs after the comments have been posted, so it
        // cannot be the thing that stops this.
        if (leaseStore is not null && !await leaseStore.TryMarkPublishingAsync(job.Id, ct: ct))
        {
            if (protocolId.HasValue)
            {
                await protocolRecorder.SetCompletedAsync(protocolId.Value, "Excluded", 0, 0, 0, 0, null, ct);
            }

            LogPublicationAbandoned(logger, job.Id);
            return;
        }

        try
        {
            var publicationResult = this.PrepareResultForPublication(job, pr, result);

            // The minimum-severity filter governs SCM PUBLICATION only. The persisted result below keeps every
            // finding, so a suppressed low-severity finding is still visible in the review record ("no comment
            // posted" is not "no review").
            var minimumSeverityToPost = await clientRegistry.GetMinimumSeverityToPostAsync(job.ClientId, ct);
            var publishResult = FilterCommentsByMinimumSeverity(publicationResult, minimumSeverityToPost);

            IReadOnlySet<string> autoResolvedThreadIds = new HashSet<string>(StringComparer.Ordinal);
            var scmCommentPostingEnabled = await clientRegistry.GetScmCommentPostingEnabledAsync(job.ClientId, ct);
            var publicationIdentity = ResolvePublicationIdentity(job, pr);
            var diagnostics = ReviewCommentPostingDiagnosticsDto.Empty(
                publicationResult.Comments.Count + publicationResult.CarriedForwardCandidatesSkipped,
                publicationResult.CarriedForwardCandidatesSkipped);

            if (scmCommentPostingEnabled)
            {
                var publicationRevision = await this.ResolvePublicationReviewRevisionAsync(job, ct);
                var publicationContext = BuildPublicationContext(
                    job,
                    pr,
                    publicationRevision,
                    publicationIdentity,
                    compareToIterationId);
                diagnostics = await providerRegistry.GetCodeReviewPublicationService(job.Provider)
                    .PublishReviewAsync(
                        job.ClientId,
                        job.CodeReviewReference,
                        publicationRevision,
                        publishResult,
                        publicationIdentity,
                        ct,
                        publicationContext);

                autoResolvedThreadIds = await this.AutoResolvePostedThreadsAsync(job, publishResult, diagnostics, ct);
            }

            await jobs.SetResultAsync(job.Id, publicationResult, ct);

            await this.RecordPostedCommentOriginsAsync(job, diagnostics, ct);

            // Both take the list the poster actually worked from. Pairing findings to the threads they became,
            // and reading back the ordinals the poster stamped, are only correct against that same list: the
            // minimum-severity filter above can drop findings, and every one it drops shifts the alignment.
            await this.IndexPostedFindingsAsync(job, publishResult, diagnostics, autoResolvedThreadIds, ct);

            await this.CollectCodeInsightFindingsAsync(job, pr, publicationResult, diagnostics, ct);

            if (protocolId.HasValue)
            {
                await this.RecordPostingDiagnosticsAsync(protocolId.Value, diagnostics, ct);
                await this.RecordSuppressedFindingsAsync(
                    protocolId.Value,
                    diagnostics,
                    MapPublishedOrdinalsToPersisted(publicationResult.Comments, publishResult.Comments),
                    ct);
                await protocolRecorder.SetCompletedAsync(protocolId.Value, "Completed", 0, 0, 0, 0, null, ct);
            }

            LogReviewCompleted(logger, job.Id);
        }
        catch (Exception ex)
        {
            if (protocolId.HasValue)
            {
                // A total publication failure still carries the per-thread provider errors — record them so the
                // diagnostics are not lost when nothing posted.
                if (ex is ReviewCommentPublicationFailedException publicationFailure)
                {
                    await this.RecordPostingDiagnosticsAsync(protocolId.Value, publicationFailure.Diagnostics, ct);
                }

                await protocolRecorder.RecordMemoryEventAsync(
                    protocolId.Value,
                    "memory_operation_failed",
                    JsonSerializer.Serialize(
                        new
                        {
                            operation = "publish_review_result",
                            jobId = job.Id,
                            pullRequestId = job.PullRequestId,
                            iterationId = job.IterationId,
                            repositoryId = job.RepositoryId,
                            clientId = job.ClientId,
                            errorType = ex.GetType().FullName,
                            errorMessage = ex.Message,
                        }),
                    $"Failed while posting the review result: {ex.Message}",
                    ct);
                await protocolRecorder.SetCompletedAsync(protocolId.Value, "Failed", 0, 0, 0, 0, null, ct);
            }

            throw;
        }
        finally
        {
            if (leaseStore is not null)
            {
                // Publication is over either way, so the job stops being protected from reclaim. Uses a
                // token of its own: a cancelled review still has to give this protection back.
                await leaseStore.ClearPublishingAsync(job.Id, CancellationToken.None);
            }
        }
    }

    // Resolve the optional configured trigger reviewer. It says which pull requests ProPR is asked to
    // review and is offered to the provider as a reviewer on the pull request; it decides nothing about
    // which threads are ProPR's.
    private async Task<ResolvedReviewerContext> ResolveReviewerAsync(
        ReviewJob job,
        CancellationToken ct)
    {
        var configuredTriggerReviewer = await clientRegistry.GetReviewerIdentityAsync(job.ClientId, job.ProviderHost, ct);
        return new ResolvedReviewerContext(configuredTriggerReviewer);
    }

    // T070: Resolve per-client AI connection — returns null when not configured (caller sets job failed).
    private async Task<(IChatClient ChatClient, AgentReviewRuntimeCapabilities Capabilities, string? LogicalModelName)?> ResolveAiConnectionAsync(
        ReviewJob job, CancellationToken ct)
    {
        if (aiRuntimeResolver is not null)
        {
            try
            {
                var runtime = await aiRuntimeResolver.ResolveChatRuntimeAsync(job.ClientId, AiPurpose.ReviewDefault, ct);
                job.SetAiConfig(runtime.Connection.Id, runtime.Model.RemoteModelId, job.ReviewTemperature);
                await jobs.UpdateAiConfigAsync(job.Id, runtime.Connection.Id, runtime.Model.RemoteModelId, ct, job.ReviewTemperature);
                return (runtime.ChatClient, runtime.Capabilities, runtime.LogicalModelName);
            }
            catch (Exception ex)
            {
                LogNoAiConnectionConfigured(logger, job.ClientId, job.Id);
                await jobs.SetFailedAsync(job.Id, ex.Message, ct);
                return null;
            }
        }

        var activeConnection = await aiConnectionRepository.GetActiveForClientAsync(job.ClientId, ct);
        if (activeConnection is null)
        {
            LogNoAiConnectionConfigured(logger, job.ClientId, job.Id);
            await jobs.SetFailedAsync(
                job.Id,
                $"No active AI connection configured for client {job.ClientId}. Configure one via the admin UI.",
                ct);
            return null;
        }

        var effectiveModelId = activeConnection.GetBoundModelId(AiPurpose.ReviewDefault)
                               ?? activeConnection.ConfiguredModels.FirstOrDefault(model => model.SupportsChat)?.RemoteModelId;
        if (string.IsNullOrWhiteSpace(effectiveModelId))
        {
            await jobs.SetFailedAsync(
                job.Id,
                $"Active AI connection for client {job.ClientId} has no model deployment selected. Activate a deployment in the admin UI.",
                ct);
            return null;
        }

        var client = aiChatClientFactory.CreateClient(activeConnection.BaseUrl, activeConnection.Secret);
        job.SetAiConfig(activeConnection.Id, effectiveModelId, job.ReviewTemperature);
        await jobs.UpdateAiConfigAsync(job.Id, activeConnection.Id, effectiveModelId, ct, job.ReviewTemperature);
        // Legacy (non-logical-model) resolution path — no logical model in play.
        return (client, new AgentReviewRuntimeCapabilities(false, false, false, false), null);
    }

    // Load scan state: whether a new revision exists, the reusable carry-forward baseline
    // (with whether it covered its full revision), any same-revision resume job, and the provider-neutral
    // delta-compare handle when the baseline is full-coverage.
    private async Task<(
        bool isNewIteration,
        ReviewJob? baselineJob,
        bool baselineIsFullCoverage,
        ReviewJob? resumeJob,
        int? compareToIterationId,
        ReviewRevision? compareToReviewRevision)> LoadScanStateAsync(
        ReviewJob job,
        CancellationToken ct)
    {
        // Delegated to the shared reuse service so the dispatch path adopts exactly what this path
        // adopts. Two implementations of "what may this review inherit" is how a remote review quietly
        // becomes a different review.
        var state = await this.Reuse.LoadScanStateAsync(job, ct);
        return (
            state.IsNewIteration,
            state.BaselineJob,
            state.BaselineIsFullCoverage,
            state.ResumeJob,
            state.CompareToIterationId,
            state.CompareToReviewRevision);
    }

    // T072: Fetch PR and guard the active status — returns null if PR is no longer active (job already updated).
    private async Task<PullRequestRef> FetchPullRequestRefAsync(ReviewJob job, CancellationToken ct)
    {
        return await prFetcher.FetchRefAsync(
            job.OrganizationUrl,
            job.ProjectId,
            job.RepositoryId,
            job.PullRequestId,
            job.ClientId,
            ct);
    }

    private static async Task DisposeEarlyWorkspaceAsync(
        IReviewRepositoryWorkspace? workspace,
        ReviewRepositoryWorkspacePreparationResult preparation)
    {
        if (workspace is not null && preparation.Succeeded)
        {
            await workspace.DisposeAsync();
        }
    }

    private async Task<ReviewRepositoryWorkspacePreparationResult> PrepareWorkspaceForFetchAsync(
        ReviewJob job,
        PullRequestRef prRef,
        CancellationToken ct)
    {
        if (workspaceManager is null)
        {
            throw new InvalidOperationException("No workspace manager is registered. Local review workspace support is required.");
        }

        return await workspaceManager.PrepareAsync(
            new ReviewRepositoryWorkspaceRequest(
                job.Id,
                job.ClientId,
                job.Provider,
                job.OrganizationUrl,
                job.CodeReviewReference.Repository,
                job.PullRequestId,
                job.ReviewRevisionReference ?? throw new InvalidOperationException("A review revision is required for local workspace preparation."),
                prRef.SourceBranch,
                prRef.TargetBranch),
            ct);
    }

    private async Task<PullRequest?> FetchPullRequestAsync(
        ReviewJob job,
        int? compareToIterationId,
        ReviewRevision? compareToReviewRevision,
        IReviewRepositoryWorkspace? workspace,
        CancellationToken ct)
    {
        var pr = await prFetcher.FetchAsync(
            job.OrganizationUrl,
            job.ProjectId,
            job.RepositoryId,
            job.PullRequestId,
            job.IterationId,
            compareToIterationId,
            job.ClientId,
            ct,
            compareToReviewRevision,
            workspace);

        if (pr.Status == PrStatus.Active)
        {
            return pr;
        }

        LogPrNoLongerActive(logger, job.PullRequestId, pr.Status, job.Id);
        if (pr.Status == PrStatus.Abandoned)
        {
            LogPrAbandonedCancellingJob(logger, job.PullRequestId, job.Id);
            await jobs.SetCancelledAsync(job.Id, ct);
        }
        else
        {
            await jobs.SetFailedAsync(job.Id, "PR was closed or abandoned before review could begin", ct);
        }

        return null;
    }

    // Build review context — reuse prior results, fetch instructions and exclusions.
    // Returns (systemContext, carriedForwardPaths); systemContext is null when all files were carried
    // forward with an empty delta (no AI review needed — caller should save scan and delete job).
    private async Task<(ReviewSystemContext? systemContext, List<string> carriedForwardPaths)> BuildReviewContextAsync(
        ReviewJob job,
        PullRequest pr,
        ReviewJob? baselineJob,
        bool baselineIsFullCoverage,
        ReviewJob? resumeJob,
        IChatClient chatClient,
        AgentReviewRuntimeCapabilities runtimeCapabilities,
        string? defaultReviewLogicalModelName,
        ReviewRepositoryWorkspacePreparationResult preparedWorkspace,
        CancellationToken ct)
    {
        var changedFilePaths = pr.ChangedFiles.Select(f => f.Path).ToList();
        var changedPathsSet = new HashSet<string>(changedFilePaths, StringComparer.OrdinalIgnoreCase);

        // Fetch exclusion rules up front: on the partial-baseline (full-fetch) path a baseline-reviewed
        // file that now matches an exclusion rule must be excluded rather than carried forward stale.
        var exclusionRules = await this.FetchExclusionRulesAsync(job, pr, ct);

        // Same-revision resume (files changed at this revision) and cross-revision carry-forward (unchanged
        // files) must never both write a result row for the same path. Resume runs first so a result computed
        // at the current revision wins over an inherited one from an earlier revision.
        // Case-insensitive to match changedPathsSet so the no-duplicate guarantee holds even when resume and
        // carry-forward emit the same logical path in different casing (e.g. src/File.cs vs src/file.cs).
        var claimedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await this.Reuse.ResumePriorFileResultsAsync(job, resumeJob, changedPathsSet, claimedPaths, ct);
        var carriedForwardPaths = await this.Reuse.CarryForwardBaselineResultsAsync(
            job, baselineJob, baselineIsFullCoverage, changedPathsSet, exclusionRules, claimedPaths, ct);

        if (changedFilePaths.Count == 0 && (carriedForwardPaths.Count > 0 || resumeJob is not null))
        {
            return (null, carriedForwardPaths);
        }

        var customSystemMessage = await clientRegistry.GetCustomSystemMessageAsync(job.ClientId, ct);
        var enableEvidenceBackedVerification = await clientRegistry.GetEvidenceBackedVerificationEnabledAsync(job.ClientId, ct);
        var enableLanguageRobustScreening = await clientRegistry.GetLanguageRobustScreeningEnabledAsync(job.ClientId, ct);
        var enableMultiPassUnion = await clientRegistry.GetMultiPassUnionEnabledAsync(job.ClientId, ct);
        var includeLinkedItemsInContext = await clientRegistry.GetIncludeLinkedItemsInContextEnabledAsync(job.ClientId, ct);
        var reviewPasses = await clientRegistry.GetReviewPassesAsync(job.ClientId, ct);
        var baselineReasoningEffort = await clientRegistry.GetBaselineReasoningEffortAsync(job.ClientId, ct);

        // The output language is resolved once for the job and carried on the context, so every prose stage of this
        // review states the same language rather than each call inheriting whatever language its own input happened
        // to be in.
        var outputLanguage = await clientRegistry.GetOutputLanguageAsync(job.ClientId, ct);

        var workspacePreparation = preparedWorkspace;

        if (!workspacePreparation.Succeeded)
        {
            var failure = workspacePreparation.Failure;
            throw new InvalidOperationException($"Local review workspace preparation failed at stage '{failure?.Stage}' ({failure?.Code}): {failure?.Message}");
        }

        await this.RecordWorkspaceProtocolAsync(job, workspacePreparation, ct);

        var reviewTools = reviewContextToolsFactory.Create(
            new ReviewContextToolsRequest(
                job.CodeReviewReference,
                pr.SourceBranch,
                job.IterationId,
                job.ClientId,
                job.ProCursorSourceScopeMode == ProCursorSourceScopeMode.SelectedSources
                    ? job.ProCursorSourceIds
                    : null,
                job.OrganizationUrl,
                pr.TargetBranch,
                pr.ChangedFiles.Select(ChangedPathSnapshot.FromChangedFile).ToList().AsReadOnly(),
                Workspace: workspacePreparation.Workspace,
                WorkspaceLease: workspacePreparation.Workspace?.Lease,
                WorkspaceFailure: workspacePreparation.Failure));
        var fetchedInstructions = await instructionFetcher.FetchAsync(
            job.OrganizationUrl,
            job.ProjectId,
            job.RepositoryId,
            pr.TargetBranch,
            job.ClientId,
            ct);
        var relevantInstructions = fetchedInstructions.Count > 0
            ? await instructionEvaluator.EvaluateRelevanceAsync(fetchedInstructions, changedFilePaths, ct)
            : [];

        var systemContext = new ReviewSystemContext(customSystemMessage, relevantInstructions, reviewTools)
        {
            DefaultReviewChatClient = chatClient,
            DefaultReviewModelId = job.AiModel,
            LogicalModelName = defaultReviewLogicalModelName,
            RuntimeCapabilities = runtimeCapabilities,
            EnableEvidenceBackedVerification = enableEvidenceBackedVerification,
            EnableLanguageRobustScreening = enableLanguageRobustScreening,
            EnableMultiPassUnion = enableMultiPassUnion,
            IncludeLinkedItemsInContext = includeLinkedItemsInContext,
            ReviewPasses = reviewPasses,
            BaselineReasoningEffort = baselineReasoningEffort,
            ExclusionRules = exclusionRules,
            ModelId = job.AiModel,
            ProtocolRecorder = protocolRecorder,
            Temperature = job.ReviewTemperature,
            PromptOverrides = await LoadPromptOverridesAsync(job.ClientId, promptOverrideService, logger, ct),
            ReviewWorkspace = workspacePreparation.Workspace,
            OutputLanguage = outputLanguage,
        };

        return (systemContext, carriedForwardPaths);
    }

    // Discovers the work items / issues linked to the pull request (when the client opted in) and attaches a
    // bounded, deduplicated summary to the PullRequest so it renders into the review prompt. Fail-soft: any
    // discovery error leaves the review to proceed without linked-item context. Never logs item titles/bodies.
    private async Task<PullRequest> AttachLinkedItemsAsync(
        ReviewJob job,
        PullRequest pr,
        ReviewSystemContext systemContext,
        CancellationToken ct)
    {
        if (!systemContext.IncludeLinkedItemsInContext)
        {
            return pr;
        }

        try
        {
            var provider = providerRegistry.GetLinkedItemProvider(job.Provider);
            var discovered = await provider.DiscoverLinkedItemsAsync(job.ClientId, pr, ct);
            var bounded = LinkedItemContextBounding.Bound(
                discovered,
                this._opts.MaxLinkedItemsInContext,
                this._opts.MaxLinkedItemDescriptionChars,
                out var droppedCount);

            if (bounded.Count == 0)
            {
                return pr;
            }

            LogLinkedItemsAttached(logger, job.Id, discovered.Count, bounded.Count, droppedCount);
            return pr with { LinkedItems = bounded };
        }
        catch (Exception ex)
        {
            LogLinkedItemsSkipped(logger, job.Id, ex);
            return pr;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message =
            "Attached linked items to the review context for job {JobId}: {DiscoveredCount} discovered, {InjectedCount} injected, {DroppedCount} dropped by cap.")]
    private static partial void LogLinkedItemsAttached(
        ILogger logger,
        Guid jobId,
        int discoveredCount,
        int injectedCount,
        int droppedCount);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Linked-item discovery unavailable for job {JobId}; proceeding without linked-item context.")]
    private static partial void LogLinkedItemsSkipped(ILogger logger, Guid jobId, Exception ex);

    // Fetches the repository exclusion rules for the review target branch. IRepositoryExclusionFetcher is
    // contractually non-throwing and returns defaults on failure; the catch is belt-and-suspenders.
    private async Task<ReviewExclusionRules> FetchExclusionRulesAsync(ReviewJob job, PullRequest pr, CancellationToken ct)
    {
        try
        {
            return await exclusionFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                pr.TargetBranch,
                job.ClientId,
                ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch exclusion rules for job {JobId}; using defaults", job.Id);
            return ReviewExclusionRules.Default;
        }
    }

    private async Task RecordWorkspaceProtocolAsync(
        ReviewJob job,
        ReviewRepositoryWorkspacePreparationResult workspacePreparation,
        CancellationToken ct)
    {
        if (!workspacePreparation.Succeeded && workspacePreparation.Failure is null)
        {
            return;
        }

        if (protocolRecorder is null)
        {
            return;
        }

        var protocolId = jobs.GetById(job.Id)?.Protocols
            .OrderByDescending(protocol => protocol.StartedAt)
            .FirstOrDefault()?.Id;
        if (!protocolId.HasValue)
        {
            return;
        }

        if (workspacePreparation.Workspace is not null)
        {
            var details = JsonSerializer.Serialize(
                new
                {
                    attempted = true,
                    prepared = true,
                    fallbackApplied = false,
                    workspaceKey = workspacePreparation.Workspace.Lease.WorkspaceKey,
                });
            await protocolRecorder.RecordReviewStrategyEventAsync(
                protocolId.Value,
                LocalWorkspacePreparedEventName,
                details,
                details,
                null,
                ct);
            return;
        }

        if (workspacePreparation.Failure is not null)
        {
            var failureDetails = JsonSerializer.Serialize(
                new
                {
                    attempted = true,
                    prepared = false,
                    fallbackApplied = true,
                    stage = workspacePreparation.Failure.Stage,
                    code = workspacePreparation.Failure.Code,
                    message = workspacePreparation.Failure.Message,
                });
            await protocolRecorder.RecordReviewStrategyEventAsync(
                protocolId.Value,
                LocalWorkspaceFailedEventName,
                failureDetails,
                failureDetails,
                null,
                ct);
            await protocolRecorder.RecordReviewStrategyEventAsync(
                protocolId.Value,
                LocalWorkspaceFallbackAppliedEventName,
                failureDetails,
                failureDetails,
                null,
                ct);
        }
    }

    // T075: Dispatch the file-by-file review and merge carry-forward paths into the result.
    private async Task<ReviewResult> DispatchFileReviewAsync(
        ReviewJob job,
        PullRequest pr,
        ReviewSystemContext systemContext,
        IChatClient chatClient,
        CancellationToken ct)
    {
        try
        {
            return await fileByFileReviewOrchestrator.ReviewAsync(job, pr, systemContext, ct, chatClient);
        }
        finally
        {
            if (systemContext.ReviewWorkspace is not null)
            {
                await systemContext.ReviewWorkspace.DisposeAsync();
                systemContext.ReviewWorkspace = null;
            }
        }
    }

    private async Task HandlePartialReviewFailureAsync(
        ReviewJob job,
        PullRequest? pr,
        PartialReviewFailureException ex,
        CancellationToken ct)
    {
        LogPartialReviewFailure(logger, job.Id, ex.FailedCount, ex.TotalCount);

        job.RetryCount++;
        await jobs.UpdateRetryCountAsync(job.Id, job.RetryCount, ct);

        if (job.RetryCount >= this._opts.MaxFileReviewRetries)
        {
            // On the final retry, post any partial results from the files that succeeded
            // rather than silently discarding them.
            if (ex.PartialResult is { } partial &&
                (!string.IsNullOrWhiteSpace(partial.Summary) || partial.Comments.Count > 0))
            {
                var reviewerContext = await this.ResolveReviewerAsync(job, ct);
                try
                {
                    await this.PublishReviewResultAsync(
                        job,
                        pr ?? new PullRequest(
                            job.OrganizationUrl,
                            job.ProjectId,
                            job.RepositoryId,
                            job.RepositoryId,
                            job.PullRequestId,
                            job.IterationId,
                            string.Empty,
                            null,
                            string.Empty,
                            string.Empty,
                            [],
                            ExistingThreads: pr?.ExistingThreads),
                        partial,
                        null,
                        ct);
                    return;
                }
                catch (Exception postEx)
                {
                    LogReviewFailed(logger, job.Id, postEx);
                }
            }

            await jobs.SetFailedAsync(job.Id, $"Max retries reached. {ex.Message}", ct);
        }
        else
        {
            // Re-queue the job so the worker picks it up again without waiting for a restart.
            // FileByFileReviewOrchestrator skips already-completed file results on the next pass.
            await jobs.TryTransitionAsync(job.Id, JobStatus.Processing, JobStatus.Pending, ct);
        }
    }

    private async Task RecordPostingDiagnosticsAsync(
        Guid protocolId,
        ReviewCommentPostingDiagnosticsDto diagnostics,
        CancellationToken ct)
    {
        var summaryDetails = JsonSerializer.Serialize(
            new
            {
                candidateCount = diagnostics.CandidateCount,
                postedCount = diagnostics.PostedCount,
                suppressedCount = diagnostics.SuppressedCount,
                failedCount = diagnostics.FailedCount,
                suppressionReasons = diagnostics.SuppressionReasons,
                consideredOpenThreads = diagnostics.ConsideredOpenThreads,
                consideredResolvedThreads = diagnostics.ConsideredResolvedThreads,
                usedFallbackChecks = diagnostics.UsedFallbackChecks,
                carriedForwardCandidatesSkipped = diagnostics.CarriedForwardCandidatesSkipped,
            });

        await protocolRecorder.RecordDedupEventAsync(protocolId, "dedup_summary", summaryDetails, null, ct);

        await this.RecordPostingFailuresAsync(protocolId, diagnostics, ct);

        if (!diagnostics.IsDegraded)
        {
            return;
        }

        var degradedModeDetails = JsonSerializer.Serialize(
            new
            {
                cause = diagnostics.DegradedCause ?? "Duplicate protection ran in degraded mode.",
                degradedComponents = diagnostics.DegradedComponents,
                fallbackChecks = diagnostics.FallbackChecks,
                affectedCandidateCount = diagnostics.AffectedCandidateCount,
                reviewContinued = true,
            });

        await protocolRecorder.RecordDedupEventAsync(protocolId, "dedup_degraded_mode", degradedModeDetails, null, ct);
    }

    /// <summary>
    ///     Records each finding the pass withheld, with what it matched and how closely.
    /// </summary>
    /// <remarks>
    ///     A suppressed duplicate is kept rather than dropped, so it has to be visible somewhere. The counts in
    ///     the summary event say how many were withheld; this says which ones and on what evidence, which is
    ///     what makes a badly chosen similarity threshold detectable after the fact instead of invisible.
    /// </remarks>
    /// <summary>
    ///     Maps each position in the list handed to the poster back to its position in the persisted result.
    /// </summary>
    /// <remarks>
    ///     The poster stamps the ordinal it saw, which indexes the published list; everything that later joins
    ///     to a finding, the persisted result and the insight records alike, indexes the unfiltered one. When
    ///     the minimum-severity filter drops nothing the two agree, and when it drops anything they do not, so
    ///     the translation happens once here rather than being assumed away.
    /// </remarks>
    private static IReadOnlyList<int> MapPublishedOrdinalsToPersisted(
        IReadOnlyList<ReviewComment> persisted,
        IReadOnlyList<ReviewComment> published)
    {
        var map = new int[published.Count];
        var persistedIndex = 0;

        for (var publishedIndex = 0; publishedIndex < published.Count; publishedIndex++)
        {
            // The filter preserves order and keeps object identity, so advancing a single cursor pairs them.
            while (persistedIndex < persisted.Count
                   && !ReferenceEquals(persisted[persistedIndex], published[publishedIndex]))
            {
                persistedIndex++;
            }

            map[publishedIndex] = persistedIndex < persisted.Count ? persistedIndex : publishedIndex;
            persistedIndex++;
        }

        return map;
    }

    private async Task RecordSuppressedFindingsAsync(
        Guid protocolId,
        ReviewCommentPostingDiagnosticsDto diagnostics,
        IReadOnlyList<int> publishedToPersistedOrdinal,
        CancellationToken ct)
    {
        if (diagnostics.SuppressedFindings.Count == 0 && diagnostics.PostedFindingNearMisses.Count == 0)
        {
            return;
        }

        var details = JsonSerializer.Serialize(
            new
            {
                suppressedCount = diagnostics.SuppressedFindings.Count,

                // The findings that came closest without being withheld, so the threshold can be judged from
                // both sides of the line rather than only from the decisions it produced.
                nearMissCount = diagnostics.PostedFindingNearMisses.Count,
                nearMisses = diagnostics.PostedFindingNearMisses.Select(finding => new
                    {
                        ordinal = finding.Ordinal >= 0 && finding.Ordinal < publishedToPersistedOrdinal.Count
                            ? publishedToPersistedOrdinal[finding.Ordinal]
                            : finding.Ordinal,
                        filePath = finding.FilePath,
                        lineNumber = finding.LineNumber,
                        matchedProviderThreadId = finding.MatchedProviderThreadId,
                        matchScore = finding.MatchScore,
                    })
                    .ToList(),
                findings = diagnostics.SuppressedFindings.Select(finding => new
                    {
                        ordinal = finding.Ordinal >= 0 && finding.Ordinal < publishedToPersistedOrdinal.Count
                            ? publishedToPersistedOrdinal[finding.Ordinal]
                            : finding.Ordinal,
                        filePath = finding.FilePath,
                        lineNumber = finding.LineNumber,
                        reasonCode = finding.ReasonCode,
                        matchedProviderThreadId = finding.MatchedProviderThreadId,
                        matchScore = finding.MatchScore,
                    })
                    .ToList(),
            });

        await protocolRecorder.RecordDedupEventAsync(protocolId, "dedup_suppressed_findings", details, null, ct);
    }

    private async Task RecordPostingFailuresAsync(
        Guid protocolId,
        ReviewCommentPostingDiagnosticsDto diagnostics,
        CancellationToken ct)
    {
        foreach (var failure in diagnostics.PostingFailures)
        {
            var failureDetails = JsonSerializer.Serialize(
                new
                {
                    threadKind = failure.ThreadKind,
                    filePath = failure.FilePath,
                    line = failure.Line,
                });

            await protocolRecorder.RecordPublicationEventAsync(
                protocolId,
                "publication_thread_post_failed",
                failureDetails,
                failure.Error,
                ct);
        }
    }

    // Returns the subset of the result whose comments meet the client's minimum severity to post. The persisted
    // review result is NOT filtered — only what is handed to the SCM publication adapter.
    private static ReviewResult FilterCommentsByMinimumSeverity(ReviewResult result, CommentSeverity minimumSeverity)
    {
        // Info is the lowest rank, so an Info threshold posts everything — return the result untouched.
        if (minimumSeverity == CommentSeverity.Info || result.Comments.Count == 0)
        {
            return result;
        }

        var postable = result.Comments
            .Where(comment => comment.Severity.MeetsMinimum(minimumSeverity))
            .ToList();

        return postable.Count == result.Comments.Count
            ? result
            : result with { Comments = postable.AsReadOnly() };
    }

    // For each thread just posted whose finding severity the client marked for auto-resolution, posts an explanatory
    // reply and resolves the thread. Provider-neutral and best-effort: a provider without resolution support, a thread
    // whose id/anchor the adapter did not surface, or a single failed resolve never fails the review job.
    /// <summary>
    ///     Auto-resolves the threads this pass posted whose findings are all at or below the client's
    ///     auto-resolve severities, and returns the provider thread ids it actually closed.
    /// </summary>
    /// <remarks>
    ///     The returned set is what lets the posted-finding index record that ProPR closed a thread itself.
    ///     At the provider that is indistinguishable from a reviewer's fix, and the two must lead to opposite
    ///     suppression decisions. Only threads whose status update succeeded are reported, so a thread left
    ///     active by a failed update is not later treated as one ProPR closed.
    /// </remarks>
    private async Task<IReadOnlySet<string>> AutoResolvePostedThreadsAsync(
        ReviewJob job,
        ReviewResult publishResult,
        ReviewCommentPostingDiagnosticsDto diagnostics,
        CancellationToken ct)
    {
        var autoResolvedThreadIds = new HashSet<string>(StringComparer.Ordinal);

        if (diagnostics.PostedComments.Count == 0)
        {
            return autoResolvedThreadIds;
        }

        var autoResolveSeverities = await clientRegistry.GetAutoResolveSeveritiesAsync(job.ClientId, ct);
        if (autoResolveSeverities is not { Count: > 0 })
        {
            return autoResolvedThreadIds;
        }

        var autoResolveSet = autoResolveSeverities.ToHashSet();

        // Anchor (file, line) -> the severities of every finding published there. A posted thread is auto-resolved
        // only when EVERY finding at its anchor is in the configured set, so a higher-severity finding that shares a
        // line is never resolved by mistake. Keyed case-insensitively on the path, matching how paths are compared
        // elsewhere in the review pipeline.
        var severitiesByAnchor = publishResult.Comments
            .Where(comment => !string.IsNullOrWhiteSpace(comment.FilePath) && comment.LineNumber.HasValue)
            .GroupBy(comment => BuildAnchorKey(comment.FilePath!, comment.LineNumber!.Value), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(comment => comment.Severity).ToHashSet(),
                StringComparer.OrdinalIgnoreCase);

        IReviewThreadReplyPublisher replyPublisher;
        IReviewThreadStatusWriter statusWriter;
        try
        {
            replyPublisher = providerRegistry.GetReviewThreadReplyPublisher(job.Provider);
            statusWriter = providerRegistry.GetReviewThreadStatusWriter(job.Provider);
        }
        catch (Exception ex)
        {
            // The client configured auto-resolve but this provider has no thread-resolution adapter (only Azure
            // DevOps registers one today). Log so the no-op is visible, then degrade without failing the job.
            LogAutoResolveUnsupported(logger, job.Provider, job.Id, ex);
            return autoResolvedThreadIds;
        }

        var resolvedThreadIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var posted in diagnostics.PostedComments)
        {
            if (string.IsNullOrWhiteSpace(posted.ProviderThreadId)
                || string.IsNullOrWhiteSpace(posted.FilePath)
                || posted.Line is not > 0)
            {
                continue;
            }

            // A single created thread can surface more than one comment ref (all sharing its thread id); resolve
            // each thread at most once so the note and status update are not posted repeatedly.
            if (!resolvedThreadIds.Add(posted.ProviderThreadId))
            {
                continue;
            }

            if (!severitiesByAnchor.TryGetValue(BuildAnchorKey(posted.FilePath, posted.Line.Value), out var severities)
                || !severities.All(autoResolveSet.Contains))
            {
                continue;
            }

            var thread = new ReviewThreadRef(
                job.CodeReviewReference,
                posted.ProviderThreadId,
                posted.FilePath,
                posted.Line,
                isReviewerOwned: true);

            try
            {
                // Resolve FIRST, then post the note. If the status update fails, the thread is left active with no
                // reply — never an active thread carrying a note that (falsely) claims it was auto-resolved.
                await statusWriter.UpdateThreadStatusAsync(job.ClientId, thread, ResolvedThreadStatus, ct);
                autoResolvedThreadIds.Add(posted.ProviderThreadId);
                var noteCommentId = await replyPublisher.ReplyAsync(job.ClientId, thread, AutoResolvedNote, ct);
                await this.RecordPostedReplyOriginAsync(job, thread.ExternalThreadId, noteCommentId, ct);
            }
            catch (Exception ex)
            {
                // Never fail the job because a single thread could not be auto-resolved.
                LogAutoResolveThreadFailed(logger, posted.ProviderThreadId, job.Id, ex);
            }
        }

        return autoResolvedThreadIds;
    }

    // Canonical case-insensitive key for a comment's (file, line) anchor, so a posted thread ref matches the
    // published findings at the same location regardless of path casing.
    private static string BuildAnchorKey(string filePath, int line)
    {
        // Null separator can never appear in a file path, so a path ending in a space or digits can never
        // collide with the line number.
        return $"{NormalizeReviewPath(filePath)}\u0000{line.ToString(CultureInfo.InvariantCulture)}";
    }

    private ReviewResult PrepareResultForPublication(ReviewJob job, PullRequest pr, ReviewResult result)
    {
        if (!RequiresInsertedInlineAnchors(job.Provider) || result.Comments.Count == 0)
        {
            return result;
        }

        var insertedLinesByPath = BuildInsertedLineLookup(pr.ChangedFiles);
        var normalizedComments = new List<ReviewComment>(result.Comments.Count);
        var downgradedCount = 0;

        foreach (var comment in result.Comments)
        {
            if (!CanUseGitLabInlineAnchor(comment, insertedLinesByPath) &&
                !string.IsNullOrWhiteSpace(comment.FilePath) && comment.LineNumber.HasValue &&
                comment.LineNumber.Value > 0)
            {
                downgradedCount++;
                normalizedComments.Add(
                    new ReviewComment(
                        null,
                        null,
                        comment.Severity,
                        $"{NormalizeReviewPath(comment.FilePath)}:L{comment.LineNumber.Value}: {comment.Message}"));
                continue;
            }

            normalizedComments.Add(comment);
        }

        if (downgradedCount == 0)
        {
            return result;
        }

        logger.LogInformation(
            "Downgraded {DowngradedCount} {Provider} inline review comment(s) to overview comments for job {JobId} because the referenced lines were not inserted diff lines.",
            downgradedCount,
            job.Provider,
            job.Id);

        return result with { Comments = normalizedComments.AsReadOnly() };
    }

    private static bool RequiresInsertedInlineAnchors(ScmProvider provider)
    {
        return provider is ScmProvider.GitLab or ScmProvider.Forgejo;
    }

    private static bool CanUseGitLabInlineAnchor(
        ReviewComment comment,
        IReadOnlyDictionary<string, HashSet<int>> insertedLinesByPath)
    {
        if (string.IsNullOrWhiteSpace(comment.FilePath) || !comment.LineNumber.HasValue || comment.LineNumber.Value < 1)
        {
            return true;
        }

        return insertedLinesByPath.TryGetValue(NormalizeReviewPath(comment.FilePath), out var insertedLines)
               && insertedLines.Contains(comment.LineNumber.Value);
    }

    private static IReadOnlyDictionary<string, HashSet<int>> BuildInsertedLineLookup(IReadOnlyList<ChangedFile> changedFiles)
    {
        var lookup = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var changedFile in changedFiles)
        {
            if (changedFile.IsBinary)
            {
                continue;
            }

            lookup[NormalizeReviewPath(changedFile.Path)] = ExtractInsertedNewLines(changedFile);
        }

        return lookup;
    }

    private static HashSet<int> ExtractInsertedNewLines(ChangedFile changedFile)
    {
        var insertedLines = new HashSet<int>();
        var diffLines = changedFile.UnifiedDiff.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var hasHunkHeader = false;
        var currentNewLine = 0;

        foreach (var diffLine in diffLines)
        {
            ProcessUnifiedDiffLine(diffLine, insertedLines, ref currentNewLine, ref hasHunkHeader);
        }

        if (!hasHunkHeader && changedFile.ChangeType == ChangeType.Add)
        {
            var lineCount = CountLines(changedFile.FullContent);
            for (var lineNumber = 1; lineNumber <= lineCount; lineNumber++)
            {
                insertedLines.Add(lineNumber);
            }
        }

        return insertedLines;
    }

    // Classifies a single unified-diff line and updates the running new-file line cursor.
    private static void ProcessUnifiedDiffLine(
        string diffLine,
        HashSet<int> insertedLines,
        ref int currentNewLine,
        ref bool hasHunkHeader)
    {
        if (diffLine.StartsWith("@@", StringComparison.Ordinal))
        {
            if (TryParseUnifiedDiffNewLineStart(diffLine, out var newLineStart))
            {
                currentNewLine = newLineStart;
                hasHunkHeader = true;
            }

            return;
        }

        if (!hasHunkHeader)
        {
            return;
        }

        switch (ReviewDiffProcessor.ClassifyHunkLine(diffLine))
        {
            case HunkLineKind.Added:
                insertedLines.Add(currentNewLine);
                currentNewLine++;
                break;
            case HunkLineKind.Context:
                currentNewLine++;
                break;
            case HunkLineKind.Removed:
            case HunkLineKind.Marker:
                // Removed lines and non-payload markers occupy no new-file line.
                break;
        }
    }

    private static bool TryParseUnifiedDiffNewLineStart(string diffLine, out int newLineStart)
    {
        newLineStart = 0;

        var plusIndex = diffLine.IndexOf('+');
        if (plusIndex < 0)
        {
            return false;
        }

        var endIndex = plusIndex + 1;
        while (endIndex < diffLine.Length && char.IsDigit(diffLine[endIndex]))
        {
            endIndex++;
        }

        return endIndex > plusIndex + 1
               && int.TryParse(diffLine[(plusIndex + 1)..endIndex], out newLineStart)
               && newLineStart > 0;
    }

    private static int CountLines(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return 0;
        }

        return content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Length;
    }

    private static string NormalizeReviewPath(string path)
    {
        return path.TrimStart('/');
    }

    /// <summary>
    ///     Loads prompt overrides for every known prompt key for the given client.
    ///     Returns an empty dictionary on null service, cancellation, or any exception (graceful degradation).
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string>> LoadPromptOverridesAsync(
        Guid clientId,
        IPromptOverrideService? service,
        ILogger logger,
        CancellationToken ct)
    {
        if (service is null)
        {
            return new Dictionary<string, string>();
        }

        try
        {
            var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in PromptOverride.ValidPromptKeys)
            {
                var text = await service.GetOverrideAsync(clientId, null, key, ct);
                if (text is not null)
                {
                    overrides[key] = text;
                }
            }

            return overrides;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to load prompt overrides for client {ClientId}; review will proceed with global defaults",
                clientId);
            return new Dictionary<string, string>();
        }
    }

    /// <summary>
    ///     Records the revision this review examined, and nothing else. The per-thread counters belong to the
    ///     thread pass, which is the only thing that answers or resolves a thread.
    /// </summary>
    private async Task SaveScanAsync(ReviewJob job, CancellationToken ct)
    {
        try
        {
            var iterationKey = ReviewRevisionKeys.GetStoredKey(job.ReviewRevisionReference, job.IterationId);
            await prScanRepository.SetReviewWatermarkAsync(
                job.ClientId,
                job.RepositoryId,
                job.PullRequestId,
                iterationKey,
                ct);
        }
        catch (Exception ex)
        {
            LogScanSaveFailed(logger, job.Id, ex);
        }
    }

    private static ReviewerIdentity ResolvePublicationIdentity(ReviewJob job, PullRequest pr)
    {
        var externalUserId = pr.AuthorizedIdentityName
                             ?? pr.AuthorizedIdentityId?.ToString("D")
                             ?? $"connection:{job.ClientId:D}:{job.Provider}:{job.RepositoryId}:{job.PullRequestId}";
        var login = pr.AuthorizedIdentityName ?? externalUserId;
        var displayName = pr.AuthorizedIdentityName ?? login;
        var isBot = job.Provider is ScmProvider.GitHub && login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase);

        return new ReviewerIdentity(job.ProviderHost, externalUserId, login, displayName, isBot);
    }

    private static ReviewPublicationContext BuildPublicationContext(
        ReviewJob job,
        PullRequest pr,
        ReviewRevision revision,
        ReviewerIdentity publicationIdentity,
        int? compareToIterationId)
    {
        object? providerSpecificContext = job.Provider == ScmProvider.AzureDevOps
            ? new AzureDevOpsPublicationContext(compareToIterationId)
            : null;

        return new ReviewPublicationContext(
            job.CodeReviewReference,
            revision,
            publicationIdentity,
            pr.ExistingThreads ?? [],
            providerSpecificContext);
    }

    private async Task<ReviewRevision> ResolvePublicationReviewRevisionAsync(ReviewJob job, CancellationToken ct)
    {
        var reviewRevision = job.ReviewRevisionReference;
        if (job.Provider != ScmProvider.AzureDevOps && RequiresLiveRevisionRefresh(reviewRevision))
        {
            var latestRevision = await providerRegistry
                .GetCodeReviewQueryService(job.Provider)
                .GetLatestRevisionAsync(job.ClientId, job.CodeReviewReference, ct);

            if (latestRevision is not null)
            {
                logger.LogInformation(
                    "Refreshed invalid or missing review revision before publication for job {JobId} and provider {Provider}.",
                    job.Id,
                    job.Provider);
                return latestRevision;
            }
        }

        return ResolveReviewRevision(job);
    }

    private static bool RequiresLiveRevisionRefresh(ReviewRevision? revision)
    {
        if (revision is null)
        {
            return true;
        }

        return !LooksLikeCommitSha(revision.HeadSha)
               || !LooksLikeCommitSha(revision.BaseSha)
               || (revision.StartSha is not null && !LooksLikeCommitSha(revision.StartSha));
    }

    private static bool LooksLikeCommitSha(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length is < 7 or > 64)
        {
            return false;
        }

        foreach (var character in trimmed)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static ReviewRevision ResolveReviewRevision(ReviewJob job)
    {
        if (job.ReviewRevisionReference is { } reviewRevision)
        {
            return reviewRevision;
        }

        if (job.Provider == ScmProvider.AzureDevOps)
        {
            var legacyRevisionId = job.IterationId.ToString(CultureInfo.InvariantCulture);
            return new ReviewRevision(
                $"ado-head-{legacyRevisionId}",
                $"ado-base-{legacyRevisionId}",
                null,
                legacyRevisionId,
                null);
        }

        throw new InvalidOperationException($"Review job {job.Id} is missing normalized review revision data for provider {job.Provider}.");
    }

    private sealed record ResolvedReviewerContext(ReviewerIdentity? ConfiguredTriggerReviewer);

    /// <summary>What a completed pipeline hands back: the pull request it reviewed.</summary>
    private sealed record ReviewPipelineResult(PullRequest PullRequest);
}

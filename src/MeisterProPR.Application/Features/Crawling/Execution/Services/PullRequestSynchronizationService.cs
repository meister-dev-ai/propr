// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Crawling.Execution.Models;
using MeisterProPR.Application.Features.Crawling.Execution.Ports;
using MeisterProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterProPR.Application.Features.ReviewArchive;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Support;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.Events;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Application.Features.Crawling.Execution.Services;

/// <summary>Owns source-neutral pull-request lifecycle, thread-memory, and review-intake synchronization.</summary>
public sealed class PullRequestSynchronizationService(
    IJobRepository jobs,
    ILogger<PullRequestSynchronizationService> logger,
    IPullRequestIterationResolver? iterationResolver = null,
    IReviewerThreadStatusFetcher? threadStatusFetcher = null,
    IThreadMemoryService? threadMemoryService = null,
    IReviewPrScanRepository? prScanRepository = null,
    IClientRegistry? clientRegistry = null,
    IClientScmConnectionRepository? scmConnectionRepository = null,
    IPullRequestFetcher? pullRequestFetcher = null,
    IReviewArchiveIngestionService? reviewArchiveIngestionService = null,
    IPostedCommentOriginStore? postedCommentOriginStore = null,
    IBlockedPullRequestStore? blockedPullRequestStore = null) : IPullRequestSynchronizationService
{
    private const string ActivationSourceTagName = "pull_request.activation_source";
    private static readonly ActivitySource CrawlingActivitySource = new("MeisterProPR.Crawling", "1.0.0");
    private static readonly Meter CrawlingMeter = new("MeisterProPR", "1.0.0");

    private static readonly Counter<long> PullRequestSynchronizationCounter = CrawlingMeter.CreateCounter<long>(
        "meisterpropr_pull_request_synchronizations_total",
        "synchronizations",
        "Total number of shared pull-request synchronization passes triggered by crawl or webhook activation.");

    private static readonly Histogram<double> PullRequestSynchronizationDuration =
        CrawlingMeter.CreateHistogram<double>(
            "meisterpropr_pull_request_synchronization_duration_seconds",
            "s",
            "Duration of shared pull-request synchronization passes.");

    /// <inheritdoc />
    public async Task<PullRequestSynchronizationOutcome> SynchronizeAsync(
        PullRequestSynchronizationRequest request,
        CancellationToken ct = default)
    {
        var startedAt = Stopwatch.StartNew();
        using var activity = CrawlingActivitySource.StartActivity("pull_request.synchronize");
        activity?.SetTag(ActivationSourceTagName, request.ActivationSource.ToString().ToLowerInvariant());
        activity?.SetTag("pull_request.provider", request.Provider.ToString().ToLowerInvariant());
        activity?.SetTag("pull_request.status", request.PullRequestStatus.ToString().ToLowerInvariant());
        activity?.SetTag("pull_request.id", request.PullRequestId);
        activity?.SetTag("pull_request.repository_id", request.RepositoryId);
        activity?.SetTag("pull_request.allow_review_submission", request.AllowReviewSubmission);

        try
        {
            PullRequestSynchronizationOutcome outcome;

            if (request.PullRequestStatus != PrStatus.Active)
            {
                outcome = await this.SynchronizeLifecycleAsync(request, ct);
                return CompleteOutcome(activity, startedAt, request, outcome);
            }

            if (!request.AllowReviewSubmission)
            {
                outcome = new PullRequestSynchronizationOutcome(
                    PullRequestSynchronizationReviewDecision.None,
                    PullRequestSynchronizationLifecycleDecision.None,
                    [
                        $"No shared synchronization action was required for active PR #{request.PullRequestId} during {request.SummaryLabel}.",
                    ]);
                return CompleteOutcome(activity, startedAt, request, outcome);
            }

            if (blockedPullRequestStore is not null && await blockedPullRequestStore.IsBlockedAsync(
                    request.ClientId,
                    request.ProviderScopePath,
                    request.ProviderProjectKey,
                    request.RepositoryId,
                    request.PullRequestId,
                    ct))
            {
                logger.LogInformation(
                    "Skipping review synchronization for active PR #{PullRequestId} during {SummaryLabel}: the pull request is blocked from review processing.",
                    request.PullRequestId,
                    request.SummaryLabel);
                outcome = new PullRequestSynchronizationOutcome(
                    PullRequestSynchronizationReviewDecision.None,
                    PullRequestSynchronizationLifecycleDecision.None,
                    [
                        $"Pull request #{request.PullRequestId} is blocked from review processing; no review job was created during {request.SummaryLabel}.",
                    ]);
                return CompleteOutcome(activity, startedAt, request, outcome);
            }

            var reviewerIdentity = await this.ResolveReviewerIdentityAsync(request, ct);
            var reviewerId = ResolveReviewerId(reviewerIdentity);
            await this.RunThreadMemoryStateMachineAsync(request, reviewerId, ct);
            await this.IngestRetainedThreadsAsync(request, reviewerIdentity, reviewerId, ct);

            var iterationId = await this.ResolveIterationIdAsync(request, ct);
            activity?.SetTag("pull_request.iteration_id", iterationId);

            var currentRevisionKey = ReviewRevisionKeys.TryGetStoredKey(request.ReviewRevision);
            var activeJobReconciliation = await this.ReconcileActiveJobsAsync(request, currentRevisionKey, ct);
            if (activeJobReconciliation.DuplicateOutcome is not null)
            {
                return CompleteOutcome(activity, startedAt, request, activeJobReconciliation.DuplicateOutcome);
            }

            var reviewDecision = await this.EvaluateReviewDecisionAsync(request, reviewerId, iterationId, ct);
            if (reviewDecision is not null)
            {
                return CompleteOutcome(
                    activity,
                    startedAt,
                    request,
                    MergeOutcome(activeJobReconciliation, reviewDecision));
            }

            outcome = await this.SubmitReviewJobAsync(
                request,
                iterationId,
                currentRevisionKey,
                activeJobReconciliation,
                activity,
                ct);
            return CompleteOutcome(activity, startedAt, request, outcome);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("pull_request.error_type", ex.GetType().FullName ?? ex.GetType().Name);
            throw;
        }
    }

    private async Task<int> ResolveIterationIdAsync(PullRequestSynchronizationRequest request, CancellationToken ct)
    {
        var iterationId = request.CandidateIterationId ?? TryCreateSyntheticIterationId(request.ReviewRevision);
        if (iterationId.HasValue)
        {
            return iterationId.Value;
        }

        if (iterationResolver is null)
        {
            throw new InvalidOperationException("No pull-request iteration resolver is registered for shared synchronization.");
        }

        return await iterationResolver.GetLatestIterationIdAsync(
            request.ClientId,
            request.ProviderScopePath,
            request.ProviderProjectKey,
            request.RepositoryId,
            request.PullRequestId,
            ct);
    }

    private async Task<PullRequestSynchronizationOutcome> SubmitReviewJobAsync(
        PullRequestSynchronizationRequest request,
        int iterationId,
        string? currentRevisionKey,
        ActiveJobReconciliationResult activeJobReconciliation,
        Activity? activity,
        CancellationToken ct)
    {
        var job = new ReviewJob(
            Guid.NewGuid(),
            request.ClientId,
            request.ProviderScopePath,
            request.ProviderProjectKey,
            request.RepositoryId,
            request.PullRequestId,
            iterationId);

        job.SetReviewPipelineProfile(await this.ResolveReviewPipelineProfileIdAsync(request, ct));

        if (request.ReviewTemperature.HasValue)
        {
            job.SetAiConfig(job.AiConnectionId, job.AiModel, request.ReviewTemperature);
        }

        if (request.CodeReview is not null)
        {
            job.SetProviderReviewContext(request.CodeReview);
        }

        if (request.ReviewRevision is not null)
        {
            job.SetReviewRevision(request.ReviewRevision);
        }

        var scopeOutcome = this.TryApplyProCursorSourceScope(request, job);
        if (scopeOutcome is not null)
        {
            return MergeOutcome(activeJobReconciliation, scopeOutcome);
        }

        var addResult = await jobs.TryAddIfNoActiveDuplicateAsync(job, ct);
        if (!addResult.WasAdded)
        {
            var duplicateRevisionKey = ReviewRevisionKeys.TryGetStoredKey(job.ReviewRevisionReference);
            var duplicateActionSummary = !string.IsNullOrWhiteSpace(duplicateRevisionKey)
                ? $"Skipped duplicate active job for PR #{request.PullRequestId} at revision {duplicateRevisionKey} via {request.SummaryLabel}."
                : $"Skipped duplicate active job for PR #{request.PullRequestId} at iteration {iterationId} via {request.SummaryLabel}.";

            return new PullRequestSynchronizationOutcome(
                PullRequestSynchronizationReviewDecision.DuplicateActiveJob,
                activeJobReconciliation.LifecycleDecision,
                [
                    ..activeJobReconciliation.ActionSummaries,
                    duplicateActionSummary,
                ]);
        }

        if (addResult.CancelledSupersededJobCount > 0
            && !activeJobReconciliation.ActionSummaries.Any(summary => summary.Contains(
                "Cancelled ",
                StringComparison.OrdinalIgnoreCase)))
        {
            activeJobReconciliation = new ActiveJobReconciliationResult(
                activeJobReconciliation.DuplicateOutcome,
                PullRequestSynchronizationLifecycleDecision.CancelledActiveJobs,
                [
                    ..activeJobReconciliation.ActionSummaries,
                    $"Cancelled {addResult.CancelledSupersededJobCount} superseded active review job(s) for PR #{request.PullRequestId} before evaluating revision {currentRevisionKey} via {request.SummaryLabel}.",
                ]);
        }

        activity?.SetTag("pull_request.job_id", job.Id);

        if (request.PrTitle is not null || request.RepositoryName is not null || request.SourceBranch is not null ||
            request.TargetBranch is not null)
        {
            job.SetPrContext(request.PrTitle, request.RepositoryName, request.SourceBranch, request.TargetBranch);
            await jobs.UpdatePrContextAsync(
                job.Id,
                request.PrTitle,
                request.RepositoryName,
                request.SourceBranch,
                request.TargetBranch,
                ct);
        }

        return new PullRequestSynchronizationOutcome(
            PullRequestSynchronizationReviewDecision.Submitted,
            activeJobReconciliation.LifecycleDecision,
            [
                ..activeJobReconciliation.ActionSummaries,
                $"Submitted review intake job for PR #{request.PullRequestId} at iteration {iterationId} via {request.SummaryLabel}.",
            ]);
    }

    private static PullRequestSynchronizationOutcome CompleteOutcome(
        Activity? activity,
        Stopwatch stopwatch,
        PullRequestSynchronizationRequest request,
        PullRequestSynchronizationOutcome outcome)
    {
        stopwatch.Stop();

        var activationSource = request.ActivationSource.ToString().ToLowerInvariant();
        var reviewDecision = outcome.ReviewDecision.ToString().ToLowerInvariant();
        var lifecycleDecision = outcome.LifecycleDecision.ToString().ToLowerInvariant();
        var pullRequestStatus = request.PullRequestStatus.ToString().ToLowerInvariant();

        activity?.SetTag("pull_request.review_decision", reviewDecision);
        activity?.SetTag("pull_request.lifecycle_decision", lifecycleDecision);
        activity?.SetTag("pull_request.action_summary_count", outcome.ActionSummaries.Count);
        activity?.SetStatus(ActivityStatusCode.Ok);

        var tags = new TagList
        {
            { ActivationSourceTagName, activationSource },
            { "pull_request.status", pullRequestStatus },
            { "pull_request.review_decision", reviewDecision },
            { "pull_request.lifecycle_decision", lifecycleDecision },
        };

        PullRequestSynchronizationCounter.Add(1, tags);
        PullRequestSynchronizationDuration.Record(stopwatch.Elapsed.TotalSeconds, tags);
        return outcome;
    }

    private async Task<PullRequestSynchronizationOutcome> SynchronizeLifecycleAsync(
        PullRequestSynchronizationRequest request,
        CancellationToken ct)
    {
        var activeJobs = await jobs.GetActiveJobsForConfigAsync(
            request.ProviderScopePath,
            request.ProviderProjectKey,
            ct);
        var matchingJobs = activeJobs
            .Where(job => IsSamePullRequestTarget(job, request)
                          && job.PullRequestId == request.PullRequestId)
            .ToList();

        var pullRequestStatus = request.PullRequestStatus.ToString().ToLowerInvariant();
        if (matchingJobs.Count == 0)
        {
            return new PullRequestSynchronizationOutcome(
                PullRequestSynchronizationReviewDecision.None,
                PullRequestSynchronizationLifecycleDecision.NoActiveJobsToCancel,
                [
                    $"No active review jobs required cancellation for PR #{request.PullRequestId} because the pull request is {pullRequestStatus}.",
                ]);
        }

        foreach (var job in matchingJobs)
        {
            await jobs.SetCancelledAsync(job.Id, ct);
        }

        return new PullRequestSynchronizationOutcome(
            PullRequestSynchronizationReviewDecision.None,
            PullRequestSynchronizationLifecycleDecision.CancelledActiveJobs,
            [
                $"Cancelled {matchingJobs.Count} active review job(s) for PR #{request.PullRequestId} because the pull request is {pullRequestStatus}.",
            ]);
    }

    private async Task<PullRequestSynchronizationOutcome?> EvaluateReviewDecisionAsync(
        PullRequestSynchronizationRequest request,
        Guid? reviewerId,
        int iterationId,
        CancellationToken ct)
    {
        var existingJob = jobs.FindActiveJob(
            request.ProviderScopePath,
            request.ProviderProjectKey,
            request.RepositoryId,
            request.PullRequestId,
            iterationId);

        if (existingJob is not null)
        {
            return new PullRequestSynchronizationOutcome(
                PullRequestSynchronizationReviewDecision.DuplicateActiveJob,
                PullRequestSynchronizationLifecycleDecision.None,
                [
                    $"Skipped duplicate active job for PR #{request.PullRequestId} at iteration {iterationId} via {request.SummaryLabel}.",
                ]);
        }

        var completedSameIterationAlreadyReviewed = jobs.FindCompletedJob(
            request.ProviderScopePath,
            request.ProviderProjectKey,
            request.RepositoryId,
            request.PullRequestId,
            iterationId) is not null;

        // A prior review for this exact revision already failed and was never completed. Suppress ALL automatic
        // re-review (including same-revision thread replies) so a deterministic failure cannot loop and burn cost.
        // Only genuinely new commits (a new iteration) or a manual restart will queue another review.
        if (!completedSameIterationAlreadyReviewed
            && jobs.FindFailedJob(
                request.ProviderScopePath,
                request.ProviderProjectKey,
                request.RepositoryId,
                request.PullRequestId,
                iterationId) is not null)
        {
            logger.LogInformation(
                "Skipping automatic re-review of PR {PullRequestId} at iteration {IterationId}: a prior review failed at this revision and the pull request has not been updated. A manual restart is required.",
                request.PullRequestId,
                iterationId);
            return CreateFailedAwaitingRestartOutcome(request, iterationId);
        }

        if (prScanRepository is null || threadStatusFetcher is null)
        {
            return completedSameIterationAlreadyReviewed
                ? CreateNoReviewChangesOutcome(request, iterationId)
                : null;
        }

        try
        {
            var scan = await prScanRepository.GetAsync(
                request.ClientId,
                request.RepositoryId,
                request.PullRequestId,
                ct);
            if (scan is null)
            {
                return completedSameIterationAlreadyReviewed
                    ? CreateNoReviewChangesOutcome(request, iterationId)
                    : null;
            }

            var iterationKey = ReviewRevisionKeys.GetStoredKey(request.ReviewRevision, iterationId);
            if (!string.Equals(scan.LastProcessedCommitId, iterationKey, StringComparison.Ordinal))
            {
                return completedSameIterationAlreadyReviewed
                    ? CreateNoReviewChangesOutcome(request, iterationId)
                    : null;
            }

            var currentThreads = await threadStatusFetcher.GetReviewerThreadStatusesAsync(
                request.ProviderScopePath,
                request.ProviderProjectKey,
                request.RepositoryId,
                request.PullRequestId,
                reviewerId ?? Guid.Empty,
                request.ClientId,
                ct);

            return HasNewReviewerThreadReplies(currentThreads, scan)
                ? null
                : CreateNoReviewChangesOutcome(request, iterationId);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(
                ex,
                "Shared synchronization failed to evaluate review changes for PR {PullRequestId}; defaulting to queue review work.",
                request.PullRequestId);
            return null;
        }
    }

    private async Task<ActiveJobReconciliationResult> ReconcileActiveJobsAsync(
        PullRequestSynchronizationRequest request,
        string? currentRevisionKey,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(currentRevisionKey))
        {
            return ActiveJobReconciliationResult.None;
        }

        var activeJobs = await jobs.GetActiveJobsForConfigAsync(
            request.ProviderScopePath,
            request.ProviderProjectKey,
            ct);
        var matchingJobs = activeJobs
            .Where(job => IsSamePullRequestTarget(job, request)
                          && job.PullRequestId == request.PullRequestId)
            .ToList();
        if (matchingJobs.Count == 0)
        {
            return ActiveJobReconciliationResult.None;
        }

        var duplicateJob = matchingJobs.FirstOrDefault(job =>
            string.Equals(GetStoredRevisionKey(job), currentRevisionKey, StringComparison.Ordinal));
        var supersededJobs = matchingJobs
            .Where(job => !string.Equals(GetStoredRevisionKey(job), currentRevisionKey, StringComparison.Ordinal))
            .ToList();

        var actionSummaries = new List<string>();
        var lifecycleDecision = PullRequestSynchronizationLifecycleDecision.None;

        if (supersededJobs.Count > 0)
        {
            foreach (var supersededJob in supersededJobs)
            {
                await jobs.SetSupersededAsync(supersededJob.Id, ct);
            }

            lifecycleDecision = PullRequestSynchronizationLifecycleDecision.CancelledActiveJobs;
            actionSummaries.Add(
                $"Cancelled {supersededJobs.Count} superseded active review job(s) for PR #{request.PullRequestId} before evaluating revision {currentRevisionKey} via {request.SummaryLabel}.");
        }

        if (duplicateJob is null)
        {
            return new ActiveJobReconciliationResult(null, lifecycleDecision, actionSummaries);
        }

        actionSummaries.Add($"Skipped duplicate active job for PR #{request.PullRequestId} at revision {currentRevisionKey} via {request.SummaryLabel}.");
        return new ActiveJobReconciliationResult(
            new PullRequestSynchronizationOutcome(
                PullRequestSynchronizationReviewDecision.DuplicateActiveJob,
                lifecycleDecision,
                actionSummaries),
            lifecycleDecision,
            actionSummaries);
    }

    private static PullRequestSynchronizationOutcome MergeOutcome(
        ActiveJobReconciliationResult reconciliation,
        PullRequestSynchronizationOutcome outcome)
    {
        if (reconciliation.ActionSummaries.Count == 0
            && reconciliation.LifecycleDecision == PullRequestSynchronizationLifecycleDecision.None)
        {
            return outcome;
        }

        return new PullRequestSynchronizationOutcome(
            outcome.ReviewDecision,
            reconciliation.LifecycleDecision == PullRequestSynchronizationLifecycleDecision.None
                ? outcome.LifecycleDecision
                : reconciliation.LifecycleDecision,
            [.. reconciliation.ActionSummaries, .. outcome.ActionSummaries]);
    }

    private static string GetStoredRevisionKey(ReviewJob job)
    {
        return ReviewRevisionKeys.GetStoredKey(job.ReviewRevisionReference, job.IterationId);
    }

    private static bool IsSamePullRequestTarget(ReviewJob job, PullRequestSynchronizationRequest request)
    {
        if (request.CodeReview is not null)
        {
            return job.Provider == request.CodeReview.Repository.Host.Provider
                   && string.Equals(job.HostBaseUrl, request.CodeReview.Repository.Host.HostBaseUrl, StringComparison.Ordinal)
                   && string.Equals(job.RepositoryOwnerOrNamespace, request.CodeReview.Repository.OwnerOrNamespace, StringComparison.Ordinal)
                   && string.Equals(job.RepositoryProjectPath, request.CodeReview.Repository.ProjectPath, StringComparison.Ordinal)
                   && job.CodeReviewPlatformKind == request.CodeReview.Platform
                   && string.Equals(job.ExternalCodeReviewId, request.CodeReview.ExternalReviewId, StringComparison.Ordinal)
                   && job.PullRequestId == request.CodeReview.Number;
        }

        return string.Equals(job.RepositoryId, request.RepositoryId, StringComparison.OrdinalIgnoreCase);
    }

    private PullRequestSynchronizationOutcome? TryApplyProCursorSourceScope(
        PullRequestSynchronizationRequest request,
        ReviewJob job)
    {
        if (request.ProCursorSourceScopeMode != ProCursorSourceScopeMode.SelectedSources)
        {
            job.SetProCursorSourceScope(ProCursorSourceScopeMode.AllClientSources, []);
            return null;
        }

        var invalidSourceIds = request.InvalidProCursorSourceIds
                               ?? [];
        var selectedSourceIds = request.ProCursorSourceIds
                                ?? [];

        var invalidSourceIdsList = invalidSourceIds
            .Where(sourceId => sourceId != Guid.Empty)
            .Distinct()
            .ToList();
        if (invalidSourceIdsList.Count > 0)
        {
            return new PullRequestSynchronizationOutcome(
                PullRequestSynchronizationReviewDecision.InvalidSourceScope,
                PullRequestSynchronizationLifecycleDecision.None,
                [
                    $"Skipped review intake for PR #{request.PullRequestId} because the selected ProCursor source scope is invalid.",
                ]);
        }

        var selectedSourceIdsList = selectedSourceIds
            .Where(sourceId => sourceId != Guid.Empty)
            .Distinct()
            .ToList();
        if (selectedSourceIdsList.Count == 0)
        {
            return new PullRequestSynchronizationOutcome(
                PullRequestSynchronizationReviewDecision.EmptySourceScope,
                PullRequestSynchronizationLifecycleDecision.None,
                [
                    $"Skipped review intake for PR #{request.PullRequestId} because the selected ProCursor source scope is empty.",
                ]);
        }

        job.SetProCursorSourceScope(ProCursorSourceScopeMode.SelectedSources, selectedSourceIdsList);
        return null;
    }

    private static Guid? ResolveReviewerId(ReviewerIdentity? requestedReviewerIdentity)
    {
        if (requestedReviewerIdentity is not null)
        {
            if (Guid.TryParse(requestedReviewerIdentity.ExternalUserId, out var parsedReviewerId))
            {
                return parsedReviewerId;
            }

            return StableGuidGenerator.Create(requestedReviewerIdentity.ExternalUserId);
        }

        return null;
    }

    private async Task<ReviewerIdentity?> ResolveReviewerIdentityAsync(
        PullRequestSynchronizationRequest request,
        CancellationToken ct)
    {
        var reviewerIdentity = request.RequestedReviewerIdentity;
        if (reviewerIdentity is null && clientRegistry is not null)
        {
            var host = request.Host ?? new ProviderHostRef(request.Provider, request.ProviderScopePath);
            reviewerIdentity = await clientRegistry.GetEffectiveReviewerIdentityAsync(request.ClientId, host, ct);
        }

        return reviewerIdentity;
    }

    private static int? TryCreateSyntheticIterationId(ReviewRevision? revision)
    {
        if (revision is null)
        {
            return null;
        }

        // Providers that expose a real numeric iteration id (Azure DevOps) put it in ProviderRevisionId.
        // Trust that value directly — synthesizing a hash here would store a fake id on ReviewJob.IterationId
        // that later fails downstream provider lookups (e.g. GetPullRequestIterationAsync).
        if (int.TryParse(
                revision.ProviderRevisionId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var providerIterationId) && providerIterationId > 0)
        {
            return providerIterationId;
        }

        var key = revision.ProviderRevisionId
                  ?? revision.PatchIdentity
                  ?? $"{revision.BaseSha}::{revision.HeadSha}::{revision.StartSha}";
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(key));
        var value = BitConverter.ToInt32(hash, 0) & int.MaxValue;
        return value == 0 ? 1 : value;
    }

    private async Task RunThreadMemoryStateMachineAsync(
        PullRequestSynchronizationRequest request,
        Guid? reviewerId,
        CancellationToken ct)
    {
        if (threadStatusFetcher is null || threadMemoryService is null || prScanRepository is null)
        {
            return;
        }

        try
        {
            var scan = await prScanRepository.GetAsync(
                request.ClientId,
                request.RepositoryId,
                request.PullRequestId,
                ct);
            if (scan is null)
            {
                return;
            }

            var currentThreads = await threadStatusFetcher.GetReviewerThreadStatusesAsync(
                request.ProviderScopePath,
                request.ProviderProjectKey,
                request.RepositoryId,
                request.PullRequestId,
                reviewerId ?? Guid.Empty,
                request.ClientId,
                ct);
            if (currentThreads.Count == 0)
            {
                return;
            }

            foreach (var thread in currentThreads)
            {
                var stored = scan.Threads.FirstOrDefault(candidate => candidate.ThreadId == thread.ThreadId);
                var previousStatus = stored?.LastSeenStatus;
                var isCurrentlyResolved = IsResolvedStatus(thread.Status);
                var wasPreviouslyResolved = IsResolvedStatus(previousStatus);

                if (isCurrentlyResolved && !wasPreviouslyResolved)
                {
                    await threadMemoryService.HandleThreadResolvedAsync(
                        new ThreadResolvedDomainEvent(
                            request.ClientId,
                            request.RepositoryId,
                            request.PullRequestId,
                            thread.ThreadId,
                            thread.FilePath,
                            null,
                            thread.CommentHistory,
                            DateTimeOffset.UtcNow),
                        ct);
                }
                else if (!isCurrentlyResolved && wasPreviouslyResolved)
                {
                    await threadMemoryService.HandleThreadReopenedAsync(
                        new ThreadReopenedDomainEvent(
                            request.ClientId,
                            request.RepositoryId,
                            request.PullRequestId,
                            thread.ThreadId,
                            DateTimeOffset.UtcNow),
                        ct);
                }
            }

            await this.UpdateLastSeenStatusesAsync(scan, currentThreads, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(
                ex,
                "Shared synchronization failed while reconciling thread memory for PR {PullRequestId}.",
                request.PullRequestId);
        }
    }

    private async Task IngestRetainedThreadsAsync(
        PullRequestSynchronizationRequest request,
        ReviewerIdentity? reviewerIdentity,
        Guid? reviewerId,
        CancellationToken ct)
    {
        // Passive archive observer: only runs when an opted-in connection is resolved and the archive
        // consumer is registered. When retention is off it performs no extra work and no extra fetch.
        if (reviewArchiveIngestionService is null
            || pullRequestFetcher is null
            || scmConnectionRepository is null)
        {
            return;
        }

        try
        {
            var connection = await this.ResolveRetentionConnectionAsync(request, ct);
            if (connection is null || !connection.StoreThreads)
            {
                return;
            }

            // Fetch only the comment threads; never download changed-file content here. Thread retention
            // runs on every crawl cycle, so a full pull-request fetch would multiply the provider request
            // load and risk rate limits. Diff retention captures diffs from the review's own fetched
            // changes, not from this path.
            var threads = await pullRequestFetcher.FetchThreadsAsync(
                request.ProviderScopePath,
                request.ProviderProjectKey,
                request.RepositoryId,
                request.PullRequestId,
                request.ClientId,
                ct);

            // Resolve the originating-job provenance for the whole pull request in one pass. This is a
            // passive side-read: when the store is absent or the read fails, stamping is simply skipped and
            // ingestion proceeds with no originating job, never disrupting the crawl.
            var originatingJobs = OriginatingJobResolver.FromRows(await this.ResolveOriginatingJobsAsync(request, ct));
            foreach (var thread in threads)
            {
                var evt = BuildThreadUpdatedEvent(request, connection.Id, thread, reviewerIdentity, reviewerId, originatingJobs);
                await reviewArchiveIngestionService.HandleThreadUpdatedAsync(evt, ct);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(
                ex,
                "Thread retention ingestion failed for PR {PullRequestId}; continuing without archiving.",
                request.PullRequestId);
        }
    }

    private async Task<IReadOnlyList<PostedCommentOriginRow>> ResolveOriginatingJobsAsync(
        PullRequestSynchronizationRequest request,
        CancellationToken ct)
    {
        if (postedCommentOriginStore is null)
        {
            return [];
        }

        try
        {
            return await postedCommentOriginStore.GetJobIdsForPullRequestAsync(
                request.ClientId,
                request.RepositoryId,
                request.PullRequestId,
                ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Provenance is a passive enrichment; a lookup failure must never disrupt the crawl. Fall back
            // to no rows so retained comments are ingested without an originating job.
            logger.LogWarning(
                ex,
                "Comment-origin lookup failed for PR {PullRequestId}; ingesting retained threads without originating jobs.",
                request.PullRequestId);
            return [];
        }
    }

    private async Task<ClientScmConnectionDto?> ResolveRetentionConnectionAsync(
        PullRequestSynchronizationRequest request,
        CancellationToken ct)
    {
        if (scmConnectionRepository is null)
        {
            return null;
        }

        var host = request.Host ?? new ProviderHostRef(request.Provider, request.ProviderScopePath);
        var connections = await scmConnectionRepository.GetByClientIdAsync(request.ClientId, ct);

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
        // The request host is normalized to an authority (scheme://host[:port]); a connection's stored
        // host base URL may carry a path (e.g. an Azure DevOps organization URL). Match on the authority.
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

    private static ThreadUpdatedEvent BuildThreadUpdatedEvent(
        PullRequestSynchronizationRequest request,
        Guid connectionId,
        PrCommentThread thread,
        ReviewerIdentity? reviewerIdentity,
        Guid? reviewerId,
        OriginatingJobResolver originatingJobs)
    {
        var comments = new List<ThreadUpdatedComment>(thread.Comments.Count);
        var lastActivityAt = DateTimeOffset.MinValue;
        var threadId = thread.ThreadId.ToString(CultureInfo.InvariantCulture);

        foreach (var comment in thread.Comments)
        {
            var publishedAt = comment.PublishedAt ?? DateTimeOffset.UtcNow;
            if (publishedAt > lastActivityAt)
            {
                lastActivityAt = publishedAt;
            }

            var commentId = comment.CommentId.ToString(CultureInfo.InvariantCulture);
            // Attribute comment-id-primary: the comment id alone resolves the originating job for providers
            // whose comment ids are globally unique within the pull request (GitHub/GitLab/Forgejo), where
            // the crawled thread id need not match the recorded one. Azure DevOps scopes comment ids to a
            // thread, so several origins can share a comment id; the thread id breaks that collision.
            var originatingJobId = originatingJobs.Resolve(threadId, commentId);

            comments.Add(
                new ThreadUpdatedComment(
                    commentId,
                    ResolveAuthorIdentity(comment),
                    IsAiAuthored(comment, reviewerIdentity, reviewerId),
                    publishedAt,
                    comment.Content,
                    originatingJobId));
        }

        if (lastActivityAt == DateTimeOffset.MinValue)
        {
            lastActivityAt = DateTimeOffset.UtcNow;
        }

        return new ThreadUpdatedEvent(
            request.ClientId,
            connectionId,
            request.RepositoryId,
            request.PullRequestId,
            threadId,
            thread.FilePath,
            thread.LineNumber,
            thread.Status ?? "Active",
            lastActivityAt,
            comments);
    }

    private static string ResolveAuthorIdentity(PrThreadComment comment)
    {
        if (comment.AuthorId.HasValue && comment.AuthorId.Value != Guid.Empty)
        {
            return comment.AuthorId.Value.ToString("D");
        }

        return string.IsNullOrWhiteSpace(comment.AuthorName) ? "unknown" : comment.AuthorName;
    }

    private static bool IsAiAuthored(PrThreadComment comment, ReviewerIdentity? reviewerIdentity, Guid? reviewerId)
    {
        if (reviewerIdentity is null)
        {
            return false;
        }

        // Identity-bearing providers (Azure DevOps) stamp the author GUID; match it to the resolved
        // reviewer GUID. Other providers expose a login/display name; match on that instead.
        if (comment.AuthorId.HasValue && reviewerId.HasValue && comment.AuthorId.Value == reviewerId.Value)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(comment.AuthorName))
        {
            return false;
        }

        return string.Equals(comment.AuthorName, reviewerIdentity.Login, StringComparison.OrdinalIgnoreCase)
               || string.Equals(comment.AuthorName, reviewerIdentity.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    private async Task UpdateLastSeenStatusesAsync(
        ReviewPrScan existingScan,
        IReadOnlyList<PrThreadStatusEntry> currentThreads,
        CancellationToken ct)
    {
        if (prScanRepository is null)
        {
            return;
        }

        var updatedScan = new ReviewPrScan(
            existingScan.Id,
            existingScan.ClientId,
            existingScan.RepositoryId,
            existingScan.PullRequestId,
            existingScan.LastProcessedCommitId);

        foreach (var thread in currentThreads)
        {
            var existing = existingScan.Threads.FirstOrDefault(candidate => candidate.ThreadId == thread.ThreadId);
            updatedScan.Threads.Add(
                new ReviewPrScanThread
                {
                    ReviewPrScanId = existingScan.Id,
                    ThreadId = thread.ThreadId,
                    LastSeenReplyCount = existing?.LastSeenReplyCount ?? 0,
                    LastSeenStatus = thread.Status,
                });
        }

        foreach (var oldThread in existingScan.Threads)
        {
            if (currentThreads.Any(thread => thread.ThreadId == oldThread.ThreadId))
            {
                continue;
            }

            updatedScan.Threads.Add(
                new ReviewPrScanThread
                {
                    ReviewPrScanId = existingScan.Id,
                    ThreadId = oldThread.ThreadId,
                    LastSeenReplyCount = oldThread.LastSeenReplyCount,
                    LastSeenStatus = oldThread.LastSeenStatus,
                });
        }

        await prScanRepository.UpsertAsync(updatedScan, ct);
    }

    private static PullRequestSynchronizationOutcome CreateNoReviewChangesOutcome(
        PullRequestSynchronizationRequest request,
        int iterationId)
    {
        return new PullRequestSynchronizationOutcome(
            PullRequestSynchronizationReviewDecision.NoReviewChanges,
            PullRequestSynchronizationLifecycleDecision.None,
            [
                $"Skipped review intake for PR #{request.PullRequestId} at iteration {iterationId} because no new changes were detected via {request.SummaryLabel}.",
            ]);
    }

    private static PullRequestSynchronizationOutcome CreateFailedAwaitingRestartOutcome(
        PullRequestSynchronizationRequest request,
        int iterationId)
    {
        return new PullRequestSynchronizationOutcome(
            PullRequestSynchronizationReviewDecision.FailedAwaitingRestart,
            PullRequestSynchronizationLifecycleDecision.None,
            [
                $"Skipped automatic re-review for PR #{request.PullRequestId} at iteration {iterationId} because a prior review failed at this revision and the pull request has not been updated; a manual restart is required (via {request.SummaryLabel}).",
            ]);
    }

    private static bool HasNewReviewerThreadReplies(
        IReadOnlyList<PrThreadStatusEntry> currentThreads,
        ReviewPrScan scan)
    {
        foreach (var thread in currentThreads)
        {
            var stored = scan.Threads.FirstOrDefault(candidate => candidate.ThreadId == thread.ThreadId);
            if (stored is null)
            {
                return true;
            }

            if (thread.NonReviewerReplyCount > stored.LastSeenReplyCount)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsResolvedStatus(string? status)
    {
        return string.Equals(status, "Fixed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "Closed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "WontFix", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "ByDesign", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> ResolveReviewPipelineProfileIdAsync(
        PullRequestSynchronizationRequest request,
        CancellationToken ct)
    {
        string? configuredProfileId = null;
        if (clientRegistry is not null)
        {
            configuredProfileId = await clientRegistry.GetDefaultReviewPipelineProfileIdAsync(request.ClientId, ct);
        }

        return string.IsNullOrWhiteSpace(configuredProfileId)
            ? ReviewPipelineProfileCatalog.FileByFileBalancedProfileId
            : configuredProfileId;
    }

    private sealed record ActiveJobReconciliationResult(
        PullRequestSynchronizationOutcome? DuplicateOutcome,
        PullRequestSynchronizationLifecycleDecision LifecycleDecision,
        IReadOnlyList<string> ActionSummaries)
    {
        public static ActiveJobReconciliationResult None { get; } = new(
            null,
            PullRequestSynchronizationLifecycleDecision.None,
            []);
    }

    /// <summary>
    ///     Resolves a crawled comment back to the review job that posted it, comment-id-primary: among a
    ///     pull request's origins sharing a comment id, a single match wins outright; only a thread-local
    ///     collision (several origins under one comment id, as Azure DevOps produces) falls back to the
    ///     crawled thread id to disambiguate. Comment ids that are globally unique within the pull request
    ///     (GitHub/GitLab/Forgejo) therefore resolve on the comment id alone, ignoring a non-matching or
    ///     null crawled thread id.
    /// </summary>
    private sealed class OriginatingJobResolver
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<PostedCommentOriginRow>> _byCommentId;

        private OriginatingJobResolver(IReadOnlyDictionary<string, IReadOnlyList<PostedCommentOriginRow>> byCommentId)
        {
            this._byCommentId = byCommentId;
        }

        public static OriginatingJobResolver FromRows(IReadOnlyList<PostedCommentOriginRow> rows)
        {
            var byCommentId = rows
                .GroupBy(row => row.ProviderCommentId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<PostedCommentOriginRow>)group.ToList(),
                    StringComparer.Ordinal);
            return new OriginatingJobResolver(byCommentId);
        }

        public Guid? Resolve(string? threadId, string commentId)
        {
            if (!this._byCommentId.TryGetValue(commentId, out var matches) || matches.Count == 0)
            {
                return null;
            }

            if (matches.Count == 1)
            {
                return matches[0].JobId;
            }

            foreach (var match in matches)
            {
                if (string.Equals(match.ProviderThreadId, threadId, StringComparison.Ordinal))
                {
                    return match.JobId;
                }
            }

            return null;
        }
    }
}

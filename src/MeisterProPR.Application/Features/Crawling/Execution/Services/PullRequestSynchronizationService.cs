// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Crawling.Execution.Models;
using MeisterProPR.Application.Features.Crawling.Execution.Ports;
using MeisterProPR.Application.Features.Crawling.Webhooks.Ports;
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
    IReviewPrScanRepository? prScanRepository = null) : IPullRequestSynchronizationService
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

            var reviewerId = ResolveReviewerId(request.RequestedReviewerIdentity);
            await this.RunThreadMemoryStateMachineAsync(request, reviewerId, ct);

            var iterationId = request.CandidateIterationId;
            if (!iterationId.HasValue)
            {
                iterationId = TryCreateSyntheticIterationId(request.ReviewRevision);
            }

            if (!iterationId.HasValue)
            {
                if (iterationResolver is null)
                {
                    throw new InvalidOperationException("No pull-request iteration resolver is registered for shared synchronization.");
                }

                iterationId = await iterationResolver.GetLatestIterationIdAsync(
                    request.ClientId,
                    request.ProviderScopePath,
                    request.ProviderProjectKey,
                    request.RepositoryId,
                    request.PullRequestId,
                    ct);
            }

            activity?.SetTag("pull_request.iteration_id", iterationId.Value);

            var currentRevisionKey = ReviewRevisionKeys.TryGetStoredKey(request.ReviewRevision);
            var activeJobReconciliation = await this.ReconcileActiveJobsAsync(
                request,
                iterationId.Value,
                currentRevisionKey,
                ct);
            if (activeJobReconciliation.DuplicateOutcome is not null)
            {
                return CompleteOutcome(activity, startedAt, request, activeJobReconciliation.DuplicateOutcome);
            }

            var reviewDecision = await this.EvaluateReviewDecisionAsync(request, reviewerId, iterationId.Value, ct);
            if (reviewDecision is not null)
            {
                return CompleteOutcome(
                    activity,
                    startedAt,
                    request,
                    MergeOutcome(activeJobReconciliation, reviewDecision));
            }

            var job = new ReviewJob(
                Guid.NewGuid(),
                request.ClientId,
                request.ProviderScopePath,
                request.ProviderProjectKey,
                request.RepositoryId,
                request.PullRequestId,
                iterationId.Value);

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
                return CompleteOutcome(
                    activity,
                    startedAt,
                    request,
                    MergeOutcome(activeJobReconciliation, scopeOutcome));
            }

            await jobs.AddAsync(job, ct);
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

            outcome = new PullRequestSynchronizationOutcome(
                PullRequestSynchronizationReviewDecision.Submitted,
                activeJobReconciliation.LifecycleDecision,
                [
                    ..activeJobReconciliation.ActionSummaries,
                    $"Submitted review intake job for PR #{request.PullRequestId} at iteration {iterationId.Value} via {request.SummaryLabel}.",
                ]);

            return CompleteOutcome(activity, startedAt, request, outcome);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("pull_request.error_type", ex.GetType().FullName ?? ex.GetType().Name);
            throw;
        }
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
            .Where(job => string.Equals(job.RepositoryId, request.RepositoryId, StringComparison.OrdinalIgnoreCase)
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

        if (prScanRepository is null || threadStatusFetcher is null || !reviewerId.HasValue)
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
                reviewerId.Value,
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
        int iterationId,
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
            .Where(job => string.Equals(job.RepositoryId, request.RepositoryId, StringComparison.OrdinalIgnoreCase)
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
                await jobs.SetCancelledAsync(supersededJob.Id, ct);
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

    private static int? TryCreateSyntheticIterationId(ReviewRevision? revision)
    {
        if (revision is null)
        {
            return null;
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
        if (threadStatusFetcher is null || threadMemoryService is null || prScanRepository is null ||
            !reviewerId.HasValue)
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
                reviewerId.Value,
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
}

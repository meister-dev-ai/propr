// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterDev.ProPR.Application.Features.ReviewArchive;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Support;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Events;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using MeisterDev.ProPR.CodeInsights.Contracts;

namespace MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;

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
    IBlockedPullRequestStore? blockedPullRequestStore = null,
    ICodeInsightDispositionService? codeInsightDispositionService = null,
    ICodeInsightMissHarvester? codeInsightMissHarvester = null,
    ICodeInsightMetricSealer? codeInsightMetricSealer = null) : IPullRequestSynchronizationService
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

            // Thread memory reconciliation and the review decision both need the reviewer's threads for
            // this pass. Fetching once means one provider round trip per pull request per cycle instead
            // of two, and both consumers now reason about the same point-in-time snapshot.
            var threadStatuses = new ReviewerThreadStatusSnapshot(request, reviewerId);

            await this.RunThreadMemoryStateMachineAsync(request, threadStatuses, ct);
            await this.IngestRetainedThreadsAsync(request, reviewerIdentity, reviewerId, ct);

            var iterationId = await this.ResolveIterationIdAsync(request, ct);
            activity?.SetTag("pull_request.iteration_id", iterationId);

            var currentRevisionKey = ReviewRevisionKeys.TryGetStoredKey(request.ReviewRevision);
            var activeJobReconciliation = await this.ReconcileActiveJobsAsync(request, currentRevisionKey, ct);
            if (activeJobReconciliation.DuplicateOutcome is not null)
            {
                return CompleteOutcome(activity, startedAt, request, activeJobReconciliation.DuplicateOutcome);
            }

            var reviewDecision = await this.EvaluateReviewDecisionAsync(
                request,
                iterationId,
                threadStatuses,
                ct);
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

        // The same "nothing has changed here" rule runs again when the job executes, where it deletes the
        // job rather than recording a skip. Clearing only the intake copy would leave an explicit request
        // accepted, acknowledged with a job id, and then silently dropped, so the intent travels with the job.
        job.SetAllowUnchangedResubmission(request.AllowUnchangedResubmission);

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
                    .. activeJobReconciliation.ActionSummaries,
                    duplicateActionSummary,
                ],
                addResult.DuplicateJob?.Id);
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
                    .. activeJobReconciliation.ActionSummaries,
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
                .. activeJobReconciliation.ActionSummaries,
                $"Submitted review intake job for PR #{request.PullRequestId} at iteration {iterationId} via {request.SummaryLabel}.",
            ],
            job.Id);
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
        // The pull request has stopped being active: merged, abandoned, or closed. This is the moment the
        // correctness measurement is taken, and it runs before the job reconciliation below because the common
        // case has no active job left to cancel: the review finished long before the pull request did. The
        // sealer decides for itself whether anything is measurable, and never throws back into the crawl.
        await this.SealCodeInsightMetricAsync(request, ct);

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

    /// <summary>
    ///     Seals the pull request's code-insight measurement, once, at its first observed close. Every close
    ///     type seals identically: a finding the reviewer got right was right whether or not the pull request
    ///     was merged.
    /// </summary>
    private async Task SealCodeInsightMetricAsync(
        PullRequestSynchronizationRequest request,
        CancellationToken ct)
    {
        if (codeInsightMetricSealer is null)
        {
            return;
        }

        try
        {
            await codeInsightMetricSealer.SealAsync(
                new CodeInsightPullRequestKey(request.ClientId, request.RepositoryId, request.PullRequestId),
                request.PullRequestStatus.ToString(),
                ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // The sealer already swallows its own failures; this is the belt to that braces, because lifecycle
            // synchronization has to cancel superseded jobs whatever a measurement does.
            logger.LogWarning(
                ex,
                "Sealing the code-insight measurement for PR {PullRequestId} failed; lifecycle synchronization continues.",
                request.PullRequestId);
        }
    }

    private async Task<PullRequestSynchronizationOutcome?> EvaluateReviewDecisionAsync(
        PullRequestSynchronizationRequest request,
        int iterationId,
        ReviewerThreadStatusSnapshot threadStatuses,
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
                ],
                existingJob.Id);
        }

        // Everything below this point is change detection: heuristics that keep the automatic loop from
        // reviewing the same revision over and over. A caller that asked for this review explicitly has
        // already decided it wants the work done, so the heuristics do not apply to it. Duplicate detection
        // stays above, because two concurrent reviews of one revision are a defect under any trigger.
        if (request.AllowUnchangedResubmission)
        {
            return null;
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

            var currentThreads = await threadStatuses.GetAsync(threadStatusFetcher, ct);

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
                actionSummaries,
                duplicateJob.Id),
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
            [.. reconciliation.ActionSummaries, .. outcome.ActionSummaries],
            outcome.JobId);
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
        ReviewerThreadStatusSnapshot threadStatuses,
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

            var currentThreads = await threadStatuses.GetAsync(threadStatusFetcher, ct);
            if (currentThreads.Count == 0)
            {
                return;
            }

            foreach (var thread in currentThreads)
            {
                var stored = scan.Threads.FirstOrDefault(candidate => candidate.ThreadId == thread.ThreadId);
                var previousStatus = stored?.LastSeenStatus;
                var currentIntent = ThreadResolutionStatusInterpreter.InterpretIntent(thread.Status);
                var isCurrentlyResolved = ThreadResolutionStatusInterpreter.IsResolved(currentIntent);
                var wasPreviouslyResolved = ThreadResolutionStatusInterpreter.IsResolved(ThreadResolutionStatusInterpreter.InterpretIntent(previousStatus));

                if (isCurrentlyResolved && !wasPreviouslyResolved)
                {
                    var resolved = new ThreadResolvedDomainEvent(
                        request.ClientId,
                        request.RepositoryId,
                        request.PullRequestId,
                        thread.ThreadId,
                        thread.FilePath,
                        null,
                        thread.CommentHistory,
                        DateTimeOffset.UtcNow,
                        currentIntent,
                        thread.CodeChangedSinceRaised);

                    await threadMemoryService.HandleThreadResolvedAsync(resolved, ct);

                    // Passive code-insight observer, a sibling of thread memory rather than a change to it:
                    // a finding gets an outcome even in the cases memory deliberately refuses to store,
                    // because those are exactly the cases a quality metric needs. It never throws.
                    if (codeInsightDispositionService is not null)
                    {
                        await codeInsightDispositionService.HandleThreadResolvedAsync(resolved, ct);
                    }
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
        // Two independent passive observers read the same thread snapshots: the review archive (when the
        // producing connection opted in to thread retention) and code-insight miss harvesting (when the
        // client's collection gate is open). Each has its own precondition, so neither depends on the other
        // being switched on, but the provider fetch is shared, because this runs on every crawl cycle and a
        // second fetch would double the request load for the same data.
        if (pullRequestFetcher is null || scmConnectionRepository is null)
        {
            return;
        }

        if (reviewArchiveIngestionService is null && codeInsightMissHarvester is null)
        {
            return;
        }

        try
        {
            var connection = await this.ResolveRetentionConnectionAsync(request, ct);
            if (connection is null)
            {
                return;
            }

            var archiveWanted = reviewArchiveIngestionService is not null && connection.StoreThreads;
            var harvestWanted = codeInsightMissHarvester is not null;
            if (!archiveWanted && !harvestWanted)
            {
                return;
            }

            // Fetch only the comment threads; never download changed-file content here. This runs on every
            // crawl cycle, so a full pull-request fetch would multiply the provider request load and risk
            // rate limits. Diff retention captures diffs from the review's own fetched changes, not here.
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

            // Who ProPR posts as is learned from its own recorded posts in this pull request, not assumed from the
            // configured reviewer identity: that identity is who a review is *requested* of and need not be the
            // account whose token posts. Getting this wrong makes ProPR's own threads look human, which is a
            // false negative charged against its recall.
            var authorship = AiAuthorshipResolver.Learn(threads, originatingJobs, reviewerIdentity, reviewerId);

            foreach (var thread in threads)
            {
                var evt = BuildThreadUpdatedEvent(request, connection.Id, thread, authorship, originatingJobs);

                if (archiveWanted)
                {
                    await reviewArchiveIngestionService!.HandleThreadUpdatedAsync(evt, ct);
                }

                if (harvestWanted)
                {
                    // Human threads ProPR did not raise are what makes recall measurable; the harvester
                    // decides which of these qualify and never throws back into the crawl.
                    await codeInsightMissHarvester!.HandleThreadObservedAsync(evt, ct);
                }
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(
                ex,
                "Thread observation failed for PR {PullRequestId}; continuing without archiving or harvesting.",
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
        AiAuthorshipResolver authorship,
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
                    authorship.IsAiAuthored(comment, originatingJobId),
                    publishedAt,
                    comment.Content,
                    originatingJobId,
                    comment.IsSystemGenerated));
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

    /// <summary>
    ///     Decides which comments on a pull request are ProPR's own.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The load-bearing signal is provenance, not identity: a comment whose provider id ProPR recorded when
    ///         it posted is ProPR's, whatever account it went out as. From those comments the posting identity is
    ///         <em>learned</em>, and any other comment on the pull request by the same author is ProPR's too: which
    ///         covers threads whose ids were never recorded, and threads ProPR resolved rather than created.
    ///     </para>
    ///     <para>
    ///         The configured reviewer identity is kept as one more input and no longer the only one. It is who a
    ///         review is requested of: an account that need not be the one whose token posts, and on several
    ///         installations is not configured at all. Relying on it alone marked every ProPR thread human, and a
    ///         human thread ProPR did not raise is by definition a miss, so its own findings were being counted
    ///         against its recall.
    ///     </para>
    /// </remarks>
    private sealed class AiAuthorshipResolver
    {
        private readonly HashSet<Guid> _authorIds = [];
        private readonly HashSet<string> _authorNames = new(StringComparer.OrdinalIgnoreCase);

        public static AiAuthorshipResolver Learn(
            IReadOnlyList<PrCommentThread> threads,
            OriginatingJobResolver originatingJobs,
            ReviewerIdentity? reviewerIdentity,
            Guid? reviewerId)
        {
            var resolver = new AiAuthorshipResolver();

            if (reviewerId.HasValue)
            {
                resolver._authorIds.Add(reviewerId.Value);
            }

            if (reviewerIdentity is not null)
            {
                resolver.Remember(reviewerIdentity.Login);
                resolver.Remember(reviewerIdentity.DisplayName);
            }

            foreach (var thread in threads)
            {
                var threadId = thread.ThreadId.ToString(CultureInfo.InvariantCulture);
                foreach (var comment in thread.Comments)
                {
                    var commentId = comment.CommentId.ToString(CultureInfo.InvariantCulture);
                    if (originatingJobs.Resolve(threadId, commentId) is null)
                    {
                        continue;
                    }

                    // ProPR posted this one and recorded its id, so whatever account it appears under is the
                    // account ProPR posts as on this connection.
                    if (comment.AuthorId is { } authorId && authorId != Guid.Empty)
                    {
                        resolver._authorIds.Add(authorId);
                    }

                    resolver.Remember(comment.AuthorName);
                }
            }

            return resolver;
        }

        public bool IsAiAuthored(PrThreadComment comment, Guid? originatingJobId)
        {
            if (originatingJobId.HasValue)
            {
                return true;
            }

            if (comment.AuthorId is { } authorId && authorId != Guid.Empty && this._authorIds.Contains(authorId))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(comment.AuthorName) && this._authorNames.Contains(comment.AuthorName);
        }

        private void Remember(string? name)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                this._authorNames.Add(name);
            }
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

    /// <summary>
    ///     Holds the reviewer's thread statuses for the lifetime of a single synchronization pass so the
    ///     provider is asked once no matter how many consumers need them.
    /// </summary>
    /// <remarks>
    ///     A failed fetch is deliberately not cached: each consumer already degrades on its own terms, so
    ///     a later consumer keeps the chance to succeed exactly as it did when both fetched independently.
    /// </remarks>
    private sealed class ReviewerThreadStatusSnapshot(
        PullRequestSynchronizationRequest request,
        Guid? reviewerId)
    {
        private IReadOnlyList<PrThreadStatusEntry>? threads;

        /// <param name="fetcher">
        ///     Supplied per call because it is optional on the service and each consumer null-checks it
        ///     before reaching here. What the snapshot identifies is fixed at construction.
        /// </param>
        /// <param name="ct">Cancels the fetch.</param>
        /// <returns>The reviewer's threads for this pass.</returns>
        public async Task<IReadOnlyList<PrThreadStatusEntry>> GetAsync(
            IReviewerThreadStatusFetcher fetcher,
            CancellationToken ct)
        {
            this.threads ??= await fetcher.GetReviewerThreadStatusesAsync(
                request.ProviderScopePath,
                request.ProviderProjectKey,
                request.RepositoryId,
                request.PullRequestId,
                reviewerId ?? Guid.Empty,
                request.ClientId,
                ct);

            return this.threads;
        }
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

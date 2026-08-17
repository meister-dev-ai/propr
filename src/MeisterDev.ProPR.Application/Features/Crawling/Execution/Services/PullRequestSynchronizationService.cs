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
using MeisterDev.ProPR.Application.Features.Reviewing.Threads;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
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
    IReviewPrScanThreadStatusStore? prScanRepository = null,
    IClientRegistry? clientRegistry = null,
    IClientScmConnectionRepository? scmConnectionRepository = null,
    IPullRequestFetcher? pullRequestFetcher = null,
    IReviewArchiveIngestionService? reviewArchiveIngestionService = null,
    IPostedCommentOriginStore? postedCommentOriginStore = null,
    IBlockedPullRequestStore? blockedPullRequestStore = null,
    ICodeInsightDispositionService? codeInsightDispositionService = null,
    ICodeInsightMissHarvester? codeInsightMissHarvester = null,
    ICodeInsightMetricSealer? codeInsightMetricSealer = null,
    IThreadPassJobRepository? threadPassJobs = null,
    IScmProviderRegistry? providerRegistry = null,
    IReviewPrScanPendingReviewWriter? prScanPendingReviewWriter = null) : IPullRequestSynchronizationService
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

            // Whether a thread is ProPR's is one question with one answer for the whole pass, and every
            // consumer below asks this. It resolves on first use so a pass with no thread consumers issues
            // no provenance query at all.
            var ownership = new ThreadOwnershipSnapshot(this, request);

            // Thread memory reconciliation and the review decision both need ProPR's threads for this pass.
            // Fetching once means one provider round trip per pull request per cycle instead of two, and
            // both consumers now reason about the same point-in-time snapshot.
            var threadStatuses = new ReviewerThreadStatusSnapshot(request, ownership);

            await this.RunThreadMemoryStateMachineAsync(request, threadStatuses, ct);
            await this.IngestRetainedThreadsAsync(request, ownership, ct);

            var iterationId = await this.ResolveIterationIdAsync(request, ct);
            activity?.SetTag("pull_request.iteration_id", iterationId);

            // The conversation is decided here, above every review-intake return below, because none of them
            // speak for it: a declined increment, a reconciled duplicate and every change-detection branch
            // are all answers about the files.
            var threadPass = await this.EvaluateThreadPassAsync(request, iterationId, threadStatuses, ct);
            activity?.SetTag("pull_request.thread_pass_decision", threadPass.Decision.ToString().ToLowerInvariant());

            var subsequentIncrementSkip = await this.EvaluateSubsequentIncrementAsync(request, iterationId, ct);
            if (subsequentIncrementSkip is not null)
            {
                return CompleteOutcome(activity, startedAt, request, threadPass.ApplyTo(subsequentIncrementSkip));
            }

            var currentRevisionKey = ReviewRevisionKeys.TryGetStoredKey(request.ReviewRevision);
            var activeJobReconciliation = await this.ReconcileActiveJobsAsync(request, currentRevisionKey, ct);
            if (activeJobReconciliation.DuplicateOutcome is not null)
            {
                return CompleteOutcome(
                    activity,
                    startedAt,
                    request,
                    threadPass.ApplyTo(activeJobReconciliation.DuplicateOutcome));
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
                    threadPass.ApplyTo(MergeOutcome(activeJobReconciliation, reviewDecision)));
            }

            outcome = await this.SubmitReviewJobAsync(
                request,
                iterationId,
                currentRevisionKey,
                activeJobReconciliation,
                activity,
                ct);
            return CompleteOutcome(activity, startedAt, request, threadPass.ApplyTo(outcome));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("pull_request.error_type", ex.GetType().FullName ?? ex.GetType().Name);
            throw;
        }
    }

    /// <summary>
    ///     Decides whether the reviewer's threads on this pull request are due a visit, and queues the pass
    ///     that pays it.
    /// </summary>
    /// <remarks>
    ///     Two gates and two conditions, and nothing else. The gates are the client's comment-resolution
    ///     setting and the provider's advertised ability to write a thread status; neither the first-increment
    ///     guard nor the file pass's decision nor the existence of a review job has any say. A pass is due when
    ///     the pull request's revision differs from the revision the threads were last checked at, or when a
    ///     reviewer-owned thread has gained a non-reviewer comment since its stored count. The predicate is the
    ///     same on crawl, webhook and manual activation.
    /// </remarks>
    private async Task<ThreadPassTriggerResult> EvaluateThreadPassAsync(
        PullRequestSynchronizationRequest request,
        int iterationId,
        ReviewerThreadStatusSnapshot threadStatuses,
        CancellationToken ct)
    {
        if (threadPassJobs is null || clientRegistry is null || providerRegistry is null
            || threadStatusFetcher is null || prScanRepository is null)
        {
            return ThreadPassTriggerResult.None;
        }

        try
        {
            var behavior = await clientRegistry.GetCommentResolutionBehaviorAsync(request.ClientId, ct);
            if (behavior == CommentResolutionBehavior.Disabled)
            {
                return ThreadPassTriggerResult.ResolutionDisabled;
            }

            var capabilities = providerRegistry.GetRegisteredCapabilities(request.Provider);
            if (!ReviewThreadCapabilities.Advertises(capabilities, ReviewThreadCapabilities.Status))
            {
                return ThreadPassTriggerResult.ProviderUnsupported;
            }

            var scan = await this.TryGetScanAsync(request, ct);
            var currentThreads = await threadStatuses.GetAsync(threadStatusFetcher, ct);
            var revisionKey = ReviewRevisionKeys.GetStoredKey(request.ReviewRevision, iterationId);

            var revisionMoved = !string.Equals(
                scan?.LastThreadPassRevisionKey,
                revisionKey,
                StringComparison.Ordinal);
            if (!revisionMoved && !HasNewReviewerThreadReplies(currentThreads, scan))
            {
                return ThreadPassTriggerResult.NotDue;
            }

            var observedReplyCounts = currentThreads
                .Where(thread => !string.IsNullOrWhiteSpace(thread.ThreadId))
                .ToDictionary(
                    thread => thread.ThreadId!,
                    thread => thread.NonReviewerReplyCount,
                    StringComparer.Ordinal);

            var job = new ThreadPassJob(
                Guid.NewGuid(),
                request.ClientId,
                request.ProviderScopePath,
                request.ProviderProjectKey,
                request.RepositoryId,
                request.PullRequestId,
                iterationId,
                revisionKey,
                ThreadPassTriggerKey.Build(revisionKey, observedReplyCounts));

            if (request.CodeReview is not null)
            {
                job.SetProviderReviewContext(request.CodeReview);
            }

            var claim = await threadPassJobs.TryClaimAsync(job, ct);
            if (!claim.WasClaimed)
            {
                return new ThreadPassTriggerResult(
                    PullRequestSynchronizationThreadPassDecision.AlreadyClaimed,
                    claim.BlockingJob?.Id,
                    $"A thread pass for PR #{request.PullRequestId} is already accounted for; no second one was queued during {request.SummaryLabel}.");
            }

            return new ThreadPassTriggerResult(
                PullRequestSynchronizationThreadPassDecision.Queued,
                job.Id,
                $"Queued a thread pass for PR #{request.PullRequestId} at revision {revisionKey} via {request.SummaryLabel}.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // The conversation failing to be scheduled must never stop the files being reviewed. It must
            // equally never read as "nothing was due": a caller told None cannot tell a pull request whose
            // threads were up to date from one whose threads nobody managed to look at.
            logger.LogWarning(
                ex,
                "Shared synchronization failed to evaluate the thread pass for PR {PullRequestId}.",
                request.PullRequestId);
            return new ThreadPassTriggerResult(
                PullRequestSynchronizationThreadPassDecision.Failed,
                null,
                $"Could not decide whether PR #{request.PullRequestId} was due a thread pass during {request.SummaryLabel}: {ex.Message}");
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

        foreach (var job in matchingJobs)
        {
            await jobs.SetCancelledAsync(job.Id, ct);
        }

        // A closed pull request terminates every unit of work over it, not only the review jobs this method
        // can see through the job repository. The thread pass is its own entity with its own repository, so it
        // is cancelled here, in the same pass, before the outcome is returned.
        var cancelledThreadPasses = await this.CancelActiveThreadPassesAsync(request, ct);

        var pullRequestStatus = request.PullRequestStatus.ToString().ToLowerInvariant();
        var actionSummaries = new List<string>(2);

        if (matchingJobs.Count > 0)
        {
            actionSummaries.Add(
                $"Cancelled {matchingJobs.Count} active review job(s) for PR #{request.PullRequestId} because the pull request is {pullRequestStatus}.");
        }

        if (cancelledThreadPasses > 0)
        {
            actionSummaries.Add(
                $"Cancelled {cancelledThreadPasses} active thread pass(es) for PR #{request.PullRequestId} because the pull request is {pullRequestStatus}.");
        }

        if (actionSummaries.Count == 0)
        {
            return new PullRequestSynchronizationOutcome(
                PullRequestSynchronizationReviewDecision.None,
                PullRequestSynchronizationLifecycleDecision.NoActiveJobsToCancel,
                [
                    $"No active review jobs required cancellation for PR #{request.PullRequestId} because the pull request is {pullRequestStatus}.",
                ]);
        }

        return new PullRequestSynchronizationOutcome(
            PullRequestSynchronizationReviewDecision.None,
            PullRequestSynchronizationLifecycleDecision.CancelledActiveJobs,
            actionSummaries);
    }

    private async Task<int> CancelActiveThreadPassesAsync(
        PullRequestSynchronizationRequest request,
        CancellationToken ct)
    {
        if (threadPassJobs is null)
        {
            return 0;
        }

        try
        {
            return await threadPassJobs.CancelActiveForPullRequestAsync(
                request.ClientId,
                request.ProviderScopePath,
                request.ProviderProjectKey,
                request.RepositoryId,
                request.PullRequestId,
                ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(
                ex,
                "Cancelling thread-pass work for closed PR {PullRequestId} failed.",
                request.PullRequestId);
            return 0;
        }
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
                request.ProviderScopePath,
                request.ProviderProjectKey,
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

    /// <summary>
    ///     An automatic trigger reviews a pull request at the first revision it sees and stops there; later revisions
    ///     are left alone unless the client opted in to reviewing every increment. Returns the outcome that records
    ///     the declined increment, or <see langword="null" /> when the trigger may proceed.
    /// </summary>
    /// <remarks>
    ///     This runs before active-job reconciliation on purpose. Reconciling first would supersede the review still
    ///     running at the earlier revision and then decline to replace it, leaving the pull request unreviewed.
    /// </remarks>
    private async Task<PullRequestSynchronizationOutcome?> EvaluateSubsequentIncrementAsync(
        PullRequestSynchronizationRequest request,
        int iterationId,
        CancellationToken ct)
    {
        // Only automatic triggers are guarded. Someone who asked for this review has already decided they want the
        // work done, so both the way that request announces itself pass through: its activation source, and the flag
        // it sets to opt out of the change-detection heuristics generally.
        if (request.ActivationSource is not (PullRequestActivationSource.Crawl or PullRequestActivationSource.Webhook)
            || request.AllowUnchangedResubmission)
        {
            return null;
        }

        // Offline and minimal wirings have no registry to read the per-client setting from, so they keep the
        // unguarded behavior.
        if (clientRegistry is null)
        {
            return null;
        }

        if (await clientRegistry.GetReviewEveryIncrementEnabledAsync(request.ClientId, ct))
        {
            return null;
        }

        var engagedJobRevision = await jobs.GetLatestEngagedRevisionAsync(
            request.ClientId,
            request.ProviderScopePath,
            request.ProviderProjectKey,
            request.RepositoryId,
            request.PullRequestId,
            ct);

        // The head is compared against the revision this client engaged with, not against "some other revision".
        // A head the client already engaged with is no increment at all, and the change-detection path decides.
        var revisionKey = ReviewRevisionKeys.GetStoredKey(request.ReviewRevision, iterationId);
        if (string.Equals(engagedJobRevision?.StoredRevisionKey, revisionKey, StringComparison.Ordinal))
        {
            return null;
        }

        // A review that finds nothing deletes its own job row after writing the scan watermark, so the watermark is
        // the durable record of engagement and the job query covers only the window before it is written.
        var scan = await this.TryGetScanAsync(request, ct);
        if (string.Equals(scan?.LastProcessedCommitId, revisionKey, StringComparison.Ordinal))
        {
            return null;
        }

        // A scan record the thread pass brought into being carries no review watermark yet, and an absent
        // watermark is no engagement at all: reading it as one would decline the pull request's first review.
        var engagedRevisionKey = engagedJobRevision?.StoredRevisionKey ?? scan?.LastProcessedCommitId;
        if (string.IsNullOrEmpty(engagedRevisionKey))
        {
            return null;
        }

        logger.LogInformation(
            "Skipping review intake for PR {PullRequestId} at revision {RevisionKey} during {SummaryLabel}: this client already has a review at revision {EngagedRevisionKey} and reviews only the first increment.",
            request.PullRequestId,
            revisionKey,
            request.SummaryLabel,
            engagedRevisionKey);

        await this.RecordPendingReviewAsync(request, revisionKey, ct);

        return CreateSubsequentIncrementSkippedOutcome(request, revisionKey, engagedRevisionKey);
    }

    /// <summary>
    ///     Records the revision the guard just declined, so that a person can be shown a pull request has moved
    ///     on and offered the review of it. Nothing else knows: the head revision reaches the product only
    ///     through whoever last spoke to the provider, and the surfaces that would offer the action have not.
    /// </summary>
    /// <remarks>
    ///     Best-effort. A pull request left unreviewed is the decision that was just taken and stands whether or
    ///     not it can be advertised, so a failed write is logged and the decline returned unchanged.
    /// </remarks>
    private async Task RecordPendingReviewAsync(
        PullRequestSynchronizationRequest request,
        string revisionKey,
        CancellationToken ct)
    {
        if (prScanPendingReviewWriter is null)
        {
            return;
        }

        try
        {
            await prScanPendingReviewWriter.SetPendingReviewRevisionAsync(
                request.ClientId,
                request.ProviderScopePath,
                request.ProviderProjectKey,
                request.RepositoryId,
                request.PullRequestId,
                revisionKey,
                ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(
                ex,
                "Shared synchronization declined to review PR {PullRequestId} at revision {RevisionKey} but could not record it as awaiting a review.",
                request.PullRequestId,
                revisionKey);
        }
    }

    private async Task<ReviewPrScan?> TryGetScanAsync(
        PullRequestSynchronizationRequest request,
        CancellationToken ct)
    {
        if (prScanRepository is null)
        {
            return null;
        }

        try
        {
            return await prScanRepository.GetAsync(
                request.ClientId,
                request.ProviderScopePath,
                request.ProviderProjectKey,
                request.RepositoryId,
                request.PullRequestId,
                ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(
                ex,
                "Shared synchronization failed to read the scan watermark for PR {PullRequestId}.",
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
                request.ProviderScopePath,
                request.ProviderProjectKey,
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
                // A provider that groups its threads client-side hands back no identifier, and a transition
                // cannot be attributed to a stored row without one.
                if (string.IsNullOrWhiteSpace(thread.ThreadId))
                {
                    continue;
                }

                var stored = scan.Threads.FirstOrDefault(candidate =>
                    string.Equals(candidate.ThreadId, thread.ThreadId, StringComparison.Ordinal));
                var previousStatus = stored?.LastSeenStatus;
                var currentIntent = ThreadResolutionStatusInterpreter.InterpretIntent(thread.Status);
                var isCurrentlyResolved = ThreadResolutionStatusInterpreter.IsResolved(currentIntent);
                var wasPreviouslyResolved = ThreadResolutionStatusInterpreter.IsResolved(ThreadResolutionStatusInterpreter.InterpretIntent(previousStatus));

                if (isCurrentlyResolved && !wasPreviouslyResolved)
                {
                    var resolved = new ThreadResolvedDomainEvent(
                        request.ClientId,
                        request.ProviderScopePath,
                        request.ProviderProjectKey,
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
                            request.ProviderScopePath,
                            request.ProviderProjectKey,
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
        ThreadOwnershipSnapshot ownership,
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

            // The pass's one ownership answer, over provenance resolved for the whole pull request in a
            // single read. This is a passive side-read: when the store is absent or the read fails, nothing
            // is stamped and ingestion proceeds without originating jobs, never disrupting the crawl.
            var passOwnership = await ownership.GetAsync(ct);

            foreach (var thread in threads)
            {
                // Both consumers key on the provider's thread identity, so a thread the provider cannot name
                // has nothing to be stored or harvested under.
                if (string.IsNullOrWhiteSpace(thread.ThreadId))
                {
                    continue;
                }

                var evt = BuildThreadUpdatedEvent(request, connection.Id, thread, passOwnership);

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

    /// <summary>
    ///     Builds the pass's ownership answer from one provenance read over the whole pull request.
    /// </summary>
    /// <remarks>
    ///     No identity is known here. Nothing on this path holds a live provider connection, and no provider
    ///     persists the identity its token authenticates as. The provider adapter reached through the
    ///     thread-status fetcher contributes the identity from its own handshake into this same instance, so
    ///     consumers that run after it in the pass decide with it too; a pass that never reaches that fetcher
    ///     decides on provenance alone. Keyed on
    ///     <see cref="PullRequestSynchronizationRequest.RepositoryId" />, which is the value a review job for
    ///     this pull request carries and records provenance under.
    /// </remarks>
    private async Task<ThreadOwnershipResolver> ResolveThreadOwnershipAsync(
        PullRequestSynchronizationRequest request,
        CancellationToken ct)
    {
        if (postedCommentOriginStore is null)
        {
            return ThreadOwnershipResolver.None;
        }

        try
        {
            var provenance = await postedCommentOriginStore.GetJobIdsForPullRequestAsync(
                request.ClientId,
                request.RepositoryId,
                request.PullRequestId,
                ct);
            return ThreadOwnershipResolver.Create(
                provenance,
                ThreadOwnerIdentity.None,
                ProviderCommentIdScopes.For(request.Provider));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Provenance is a passive enrichment; a lookup failure must never disrupt the crawl. Fall back
            // to no rows so retained comments are ingested without an originating job.
            logger.LogWarning(
                ex,
                "Comment-origin lookup failed for PR {PullRequestId}; ingesting retained threads without originating jobs.",
                request.PullRequestId);
            return ThreadOwnershipResolver.None;
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
        ThreadOwnershipResolver ownership)
    {
        var comments = new List<ThreadUpdatedComment>(thread.Comments.Count);
        var lastActivityAt = DateTimeOffset.MinValue;
        var threadId = thread.ThreadId!;

        foreach (var comment in thread.Comments)
        {
            var publishedAt = comment.PublishedAt ?? DateTimeOffset.UtcNow;
            if (publishedAt > lastActivityAt)
            {
                lastActivityAt = publishedAt;
            }

            var commentId = comment.CommentId.ToString(CultureInfo.InvariantCulture);
            var commentRef = new ThreadCommentRef(threadId, commentId, comment.AuthorId, comment.AuthorName);

            comments.Add(
                new ThreadUpdatedComment(
                    commentId,
                    ResolveAuthorIdentity(comment),
                    ownership.OwnsComment(commentRef),
                    publishedAt,
                    comment.Content,
                    ownership.ResolveOriginatingJobId(threadId, commentId),
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
    ///     Holds the pass's ownership answer so the provenance behind it is read at most once, and only when
    ///     something actually asks.
    /// </summary>
    private sealed class ThreadOwnershipSnapshot(
        PullRequestSynchronizationService service,
        PullRequestSynchronizationRequest request)
    {
        private ThreadOwnershipResolver? _ownership;

        public async Task<ThreadOwnershipResolver> GetAsync(CancellationToken ct)
        {
            return this._ownership ??= await service.ResolveThreadOwnershipAsync(request, ct);
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

        var statusByThreadId = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var thread in currentThreads)
        {
            if (string.IsNullOrWhiteSpace(thread.ThreadId))
            {
                continue;
            }

            statusByThreadId[thread.ThreadId] = thread.Status;
        }

        if (statusByThreadId.Count == 0)
        {
            return;
        }

        await prScanRepository.SetLastSeenStatusesAsync(
            existingScan.ClientId,
            existingScan.OrganizationUrl,
            existingScan.ProjectId,
            existingScan.RepositoryId,
            existingScan.PullRequestId,
            statusByThreadId,
            ct);
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

    private static PullRequestSynchronizationOutcome CreateSubsequentIncrementSkippedOutcome(
        PullRequestSynchronizationRequest request,
        string revisionKey,
        string engagedRevisionKey)
    {
        return new PullRequestSynchronizationOutcome(
            PullRequestSynchronizationReviewDecision.SubsequentIncrementSkipped,
            PullRequestSynchronizationLifecycleDecision.None,
            [
                $"Skipped review intake for PR #{request.PullRequestId} at revision {revisionKey} because this client already has a review at revision {engagedRevisionKey} and reviews only the first increment (via {request.SummaryLabel}).",
            ]);
    }

    private static bool HasNewReviewerThreadReplies(
        IReadOnlyList<PrThreadStatusEntry> currentThreads,
        ReviewPrScan? scan)
    {
        foreach (var thread in currentThreads)
        {
            // A provider that groups its threads client-side hands back no identifier, so nothing stored can
            // be matched against it. Treating that as an unseen thread would report new replies on every
            // cycle and queue a pass that has nothing to key its progress on.
            if (string.IsNullOrWhiteSpace(thread.ThreadId))
            {
                continue;
            }

            var stored = scan?.Threads.FirstOrDefault(candidate =>
                string.Equals(candidate.ThreadId, thread.ThreadId, StringComparison.Ordinal));
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
    ///     Holds ProPR's thread statuses for the lifetime of a single synchronization pass so the provider is
    ///     asked once no matter how many consumers need them.
    /// </summary>
    /// <remarks>
    ///     A failed fetch is deliberately not cached: each consumer already degrades on its own terms, so
    ///     a later consumer keeps the chance to succeed exactly as it did when both fetched independently.
    /// </remarks>
    private sealed class ReviewerThreadStatusSnapshot(
        PullRequestSynchronizationRequest request,
        ThreadOwnershipSnapshot ownership)
    {
        private IReadOnlyList<PrThreadStatusEntry>? threads;

        /// <param name="fetcher">
        ///     Supplied per call because it is optional on the service and each consumer null-checks it
        ///     before reaching here. What the snapshot identifies is fixed at construction.
        /// </param>
        /// <param name="ct">Cancels the fetch.</param>
        /// <returns>ProPR's threads for this pass.</returns>
        public async Task<IReadOnlyList<PrThreadStatusEntry>> GetAsync(
            IReviewerThreadStatusFetcher fetcher,
            CancellationToken ct)
        {
            this.threads ??= await fetcher.GetReviewerThreadStatusesAsync(
                request.ProviderScopePath,
                request.ProviderProjectKey,
                request.RepositoryId,
                request.PullRequestId,
                await ownership.GetAsync(ct),
                request.ClientId,
                ct);

            return this.threads;
        }
    }

    /// <summary>
    ///     What the thread-pass trigger decided, kept apart from the file pass's decision so a caller
    ///     following one is never handed the other's job.
    /// </summary>
    /// <param name="Decision">The thread pass's own decision.</param>
    /// <param name="JobId">The thread pass this synchronization settled on, when it reached one.</param>
    /// <param name="ActionSummary">An operator-visible sentence, or null when there is nothing to say.</param>
    private sealed record ThreadPassTriggerResult(
        PullRequestSynchronizationThreadPassDecision Decision,
        Guid? JobId,
        string? ActionSummary)
    {
        public static ThreadPassTriggerResult None { get; } = new(
            PullRequestSynchronizationThreadPassDecision.None,
            null,
            null);

        public static ThreadPassTriggerResult NotDue { get; } = new(
            PullRequestSynchronizationThreadPassDecision.NotDue,
            null,
            null);

        public static ThreadPassTriggerResult ResolutionDisabled { get; } = new(
            PullRequestSynchronizationThreadPassDecision.ResolutionDisabled,
            null,
            null);

        public static ThreadPassTriggerResult ProviderUnsupported { get; } = new(
            PullRequestSynchronizationThreadPassDecision.ProviderUnsupported,
            null,
            null);

        public PullRequestSynchronizationOutcome ApplyTo(PullRequestSynchronizationOutcome outcome)
        {
            return outcome with
            {
                ThreadPassDecision = this.Decision,
                ThreadPassJobId = this.JobId,
                ActionSummaries = this.ActionSummary is null
                    ? outcome.ActionSummaries
                    : [.. outcome.ActionSummaries, this.ActionSummary],
            };
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
}

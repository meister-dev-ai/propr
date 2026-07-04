// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Crawling.Execution.Models;
using MeisterProPR.Application.Features.Crawling.Execution.Ports;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Support;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.Events;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Application.Services;

/// <summary>Orchestrates the periodic PR crawl: discovers assigned PRs and creates pending review jobs.</summary>
public sealed partial class PrCrawlService(
    ICrawlConfigurationRepository crawlConfigs,
    IAssignedReviewDiscoveryService prFetcher,
    IJobRepository jobs,
    IPrStatusFetcher prStatusFetcher,
    ILogger<PrCrawlService> logger,
    IReviewerThreadStatusFetcher? threadStatusFetcher = null,
    IThreadMemoryService? threadMemoryService = null,
    IReviewPrScanRepository? prScanRepository = null,
    IPullRequestSynchronizationService? pullRequestSynchronizationService = null,
    IProviderActivationService? providerActivationService = null,
    IClientRegistry? clientRegistry = null) : IPrCrawlService
{
    /// <summary>
    ///     Runs one crawl cycle across all active configurations, creating review jobs for newly discovered pull requests.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the crawl cycle.</param>
    public async Task CrawlAsync(CancellationToken cancellationToken = default)
    {
        var configs = await crawlConfigs.GetAllActiveAsync(cancellationToken);
        LogCrawlStarted(logger, configs.Count);

        foreach (var config in configs)
        {
            if (providerActivationService is not null &&
                !await providerActivationService.IsEnabledAsync(config.Provider, cancellationToken))
            {
                continue;
            }

            IReadOnlyList<AssignedCodeReviewRef> assignedPrs;
            ResolvedReviewer reviewerContext;
            try
            {
                assignedPrs = await prFetcher.ListAssignedOpenReviewsAsync(config, cancellationToken);
                reviewerContext = await this.ResolveReviewerContextAsync(config, cancellationToken);
                LogPrsDiscovered(logger, assignedPrs.Count, config.ProviderScopePath, config.ProviderProjectKey);
            }
            catch (Exception ex)
            {
                LogConfigFetchError(logger, config.ProviderScopePath, config.ProviderProjectKey, ex);
                continue;
            }

            await this.ProcessDiscoveredPullRequestsAsync(config, assignedPrs, reviewerContext, cancellationToken);
            await this.DetectAbandonedJobsAsync(config, assignedPrs, cancellationToken);
            await this.RunThreadMemoryStateMachineForConfigAsync(config, assignedPrs, reviewerContext, cancellationToken);
        }
    }

    private async Task ProcessDiscoveredPullRequestsAsync(
        CrawlConfigurationDto config,
        IReadOnlyList<AssignedCodeReviewRef> assignedPrs,
        ResolvedReviewer reviewerContext,
        CancellationToken cancellationToken)
    {
        foreach (var pr in assignedPrs)
        {
            if (pullRequestSynchronizationService is not null)
            {
                await this.TrySynchronizeAsync(
                    new PullRequestSynchronizationRequest
                    {
                        ActivationSource = PullRequestActivationSource.Crawl,
                        SummaryLabel = "crawl discovery",
                        ClientId = config.ClientId,
                        ProviderScopePath = config.ProviderScopePath,
                        ProviderProjectKey = config.ProviderProjectKey,
                        RepositoryId = pr.Repository.ExternalRepositoryId,
                        PullRequestId = pr.CodeReview.Number,
                        PullRequestStatus = PrStatus.Active,
                        Provider = pr.Host.Provider,
                        Host = pr.Host,
                        Repository = pr.Repository,
                        CodeReview = pr.CodeReview,
                        ReviewRevision = pr.ReviewRevision,
                        RequestedReviewerIdentity = reviewerContext.ConfiguredTriggerReviewer,
                        CandidateIterationId = pr.RevisionId,
                        PrTitle = pr.ReviewTitle,
                        RepositoryName = pr.RepositoryDisplayName,
                        SourceBranch = pr.SourceBranch,
                        TargetBranch = pr.TargetBranch,
                        ProCursorSourceScopeMode = config.ProCursorSourceScopeMode,
                        ProCursorSourceIds = config.ProCursorSourceIds ?? [],
                        InvalidProCursorSourceIds = config.InvalidProCursorSourceIds ?? [],
                        ReviewTemperature = config.ReviewTemperature,
                    },
                    cancellationToken);
                continue;
            }

            if (!await this.ShouldEnqueueReviewAsync(
                    config,
                    pr,
                    reviewerContext.EffectiveReviewerId,
                    cancellationToken))
            {
                continue;
            }

            var job = new ReviewJob(
                Guid.NewGuid(),
                config.ClientId,
                config.ProviderScopePath,
                config.ProviderProjectKey,
                pr.Repository.ExternalRepositoryId,
                pr.CodeReview.Number,
                pr.RevisionId);

            job.SelectReviewStrategy(await this.ResolveStrategySelectionAsync(config.ClientId, cancellationToken));

            if (config.ReviewTemperature.HasValue)
            {
                job.SetAiConfig(job.AiConnectionId, job.AiModel, config.ReviewTemperature);
            }

            if (pr.ReviewRevision is not null)
            {
                job.SetReviewRevision(pr.ReviewRevision);
            }

            if (!this.TryApplyProCursorSourceScope(config, job))
            {
                continue;
            }

            await jobs.AddAsync(job, cancellationToken);

            // Populate PR context snapshot from data already fetched by the crawler (no second ADO call needed).
            if (pr.ReviewTitle is not null || pr.RepositoryDisplayName is not null)
            {
                job.SetPrContext(pr.ReviewTitle, pr.RepositoryDisplayName, pr.SourceBranch, pr.TargetBranch);
                await jobs.UpdatePrContextAsync(
                    job.Id,
                    pr.ReviewTitle,
                    pr.RepositoryDisplayName,
                    pr.SourceBranch,
                    pr.TargetBranch,
                    cancellationToken);
            }

            LogJobCreated(logger, job.Id, pr.CodeReview.Number, pr.RevisionId);
        }
    }

    // --- Abandonment detection second pass ---
    // For any Pending/Processing job associated with this config that is no longer
    // present in the discovered-PR list, check the live ADO status. If the PR is
    // Abandoned, transition the job to Cancelled to stop further AI processing.
    private async Task DetectAbandonedJobsAsync(
        CrawlConfigurationDto config,
        IReadOnlyList<AssignedCodeReviewRef> assignedPrs,
        CancellationToken cancellationToken)
    {
        var activeJobs = await jobs.GetActiveJobsForConfigAsync(
            config.ProviderScopePath,
            config.ProviderProjectKey,
            cancellationToken);
        var discoveredPrIds = new HashSet<int>(assignedPrs.Select(p => p.CodeReview.Number));

        foreach (var activeJob in activeJobs)
        {
            if (discoveredPrIds.Contains(activeJob.PullRequestId))
            {
                continue;
            }

            LogAbandonmentCheckStarted(logger, activeJob.Id, activeJob.PullRequestId);

            var status = await prStatusFetcher.GetStatusAsync(
                config.ProviderScopePath,
                config.ProviderProjectKey,
                activeJob.RepositoryId,
                activeJob.PullRequestId,
                config.ClientId,
                cancellationToken);

            if (pullRequestSynchronizationService is not null)
            {
                await this.TrySynchronizeAsync(
                    new PullRequestSynchronizationRequest
                    {
                        ActivationSource = PullRequestActivationSource.Crawl,
                        SummaryLabel = "crawl disappearance",
                        ClientId = config.ClientId,
                        ProviderScopePath = config.ProviderScopePath,
                        ProviderProjectKey = config.ProviderProjectKey,
                        RepositoryId = activeJob.RepositoryId,
                        PullRequestId = activeJob.PullRequestId,
                        PullRequestStatus = status,
                        AllowReviewSubmission = false,
                    },
                    cancellationToken);
                continue;
            }

            if (status == PrStatus.Abandoned)
            {
                LogJobCancelledForAbandonedPr(logger, activeJob.PullRequestId, activeJob.Id);
                await jobs.SetCancelledAsync(activeJob.Id, cancellationToken);
            }
        }
    }

    // --- Thread memory state machine ---
    // Detects per-thread status transitions and dispatches domain events so that
    // ThreadMemoryService can store/remove embeddings independently of review jobs.
    private async Task RunThreadMemoryStateMachineForConfigAsync(
        CrawlConfigurationDto config,
        IReadOnlyList<AssignedCodeReviewRef> assignedPrs,
        ResolvedReviewer reviewerContext,
        CancellationToken cancellationToken)
    {
        if (pullRequestSynchronizationService is not null)
        {
            return;
        }

        if (threadStatusFetcher is null || threadMemoryService is null || prScanRepository is null)
        {
            LogStateMachineServicesUnavailable(
                logger,
                config.ProviderScopePath,
                config.ProviderProjectKey,
                threadStatusFetcher is null,
                threadMemoryService is null,
                prScanRepository is null);
            return;
        }

        foreach (var pr in assignedPrs)
        {
            await this.RunThreadMemoryStateMachineAsync(
                config,
                pr,
                reviewerContext.EffectiveReviewerId,
                cancellationToken);
        }
    }

    private async Task<bool> ShouldEnqueueReviewAsync(
        CrawlConfigurationDto config,
        AssignedCodeReviewRef pr,
        Guid? reviewerId,
        CancellationToken ct)
    {
        var existingJob = jobs.FindActiveJob(
            config.ProviderScopePath,
            config.ProviderProjectKey,
            pr.Repository.ExternalRepositoryId,
            pr.CodeReview.Number,
            pr.RevisionId);

        if (existingJob is not null)
        {
            LogJobAlreadyExists(logger, pr.CodeReview.Number, pr.RevisionId, existingJob.Id);
            return false;
        }

        var completedJob = jobs.FindCompletedJob(
            config.ProviderScopePath,
            config.ProviderProjectKey,
            pr.Repository.ExternalRepositoryId,
            pr.CodeReview.Number,
            pr.RevisionId);
        var completedSameIterationAlreadyReviewed = completedJob is not null;

        // A prior review for this exact revision already failed and was never completed. Suppress ALL automatic
        // re-review (including same-revision thread replies) so a deterministic failure cannot loop and burn cost.
        // Only genuinely new commits (a new iteration) or a manual restart will queue another review.
        if (!completedSameIterationAlreadyReviewed
            && jobs.FindFailedJob(
                config.ProviderScopePath,
                config.ProviderProjectKey,
                pr.Repository.ExternalRepositoryId,
                pr.CodeReview.Number,
                pr.RevisionId) is not null)
        {
            LogSkippedFailedAwaitingRestart(logger, pr.CodeReview.Number, pr.RevisionId);
            return false;
        }

        if (prScanRepository is null || threadStatusFetcher is null)
        {
            if (completedSameIterationAlreadyReviewed)
            {
                LogSkippedNoReviewChanges(logger, pr.CodeReview.Number, pr.RevisionId);
                return false;
            }

            return true;
        }

        try
        {
            var scan = await prScanRepository.GetAsync(
                config.ClientId,
                pr.Repository.ExternalRepositoryId,
                pr.CodeReview.Number,
                ct);
            if (scan is null)
            {
                if (completedSameIterationAlreadyReviewed)
                {
                    LogSkippedNoReviewChanges(logger, pr.CodeReview.Number, pr.RevisionId);
                    return false;
                }

                return true;
            }

            var iterationKey = ReviewRevisionKeys.GetStoredKey(pr.ReviewRevision, pr.RevisionId);
            if (!string.Equals(scan.LastProcessedCommitId, iterationKey, StringComparison.Ordinal))
            {
                if (completedSameIterationAlreadyReviewed)
                {
                    LogSkippedNoReviewChanges(logger, pr.CodeReview.Number, pr.RevisionId);
                    return false;
                }

                return true;
            }

            var currentThreads = await threadStatusFetcher.GetReviewerThreadStatusesAsync(
                config.ProviderScopePath,
                config.ProviderProjectKey,
                pr.Repository.ExternalRepositoryId,
                pr.CodeReview.Number,
                reviewerId ?? Guid.Empty,
                config.ClientId,
                ct);

            if (HasNewReviewerThreadReplies(currentThreads, scan))
            {
                LogSameIterationThreadChangeDetected(logger, pr.CodeReview.Number, pr.RevisionId);
                return true;
            }

            LogSkippedNoReviewChanges(logger, pr.CodeReview.Number, pr.RevisionId);
            return false;
        }
        catch (Exception ex)
        {
            LogEnqueueDecisionFailed(logger, pr.CodeReview.Number, pr.RevisionId, ex);
            return true;
        }
    }

    private async Task TrySynchronizeAsync(PullRequestSynchronizationRequest request, CancellationToken ct)
    {
        if (pullRequestSynchronizationService is null)
        {
            return;
        }

        try
        {
            await pullRequestSynchronizationService.SynchronizeAsync(request, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogSynchronizationFailed(
                logger,
                request.PullRequestId,
                request.ProviderScopePath,
                request.ProviderProjectKey,
                request.SummaryLabel,
                ex);
        }
    }

    private static bool HasNewReviewerThreadReplies(
        IReadOnlyList<PrThreadStatusEntry> currentThreads,
        ReviewPrScan scan)
    {
        foreach (var thread in currentThreads)
        {
            var stored = scan.Threads.FirstOrDefault(t => t.ThreadId == thread.ThreadId);
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

    private bool TryApplyProCursorSourceScope(CrawlConfigurationDto config, ReviewJob job)
    {
        if (config.ProCursorSourceScopeMode != ProCursorSourceScopeMode.SelectedSources)
        {
            job.SetProCursorSourceScope(ProCursorSourceScopeMode.AllClientSources, []);
            return true;
        }

        var invalidSourceIds = (config.InvalidProCursorSourceIds ?? [])
            .Where(sourceId => sourceId != Guid.Empty)
            .Distinct()
            .ToList();

        if (invalidSourceIds.Count > 0)
        {
            LogSkippedInvalidSourceScope(logger, config.Id, invalidSourceIds.Count);
            return false;
        }

        var selectedSourceIds = (config.ProCursorSourceIds ?? [])
            .Where(sourceId => sourceId != Guid.Empty)
            .Distinct()
            .ToList();

        if (selectedSourceIds.Count == 0)
        {
            LogSkippedEmptySourceScope(logger, config.Id);
            return false;
        }

        job.SetProCursorSourceScope(ProCursorSourceScopeMode.SelectedSources, selectedSourceIds);
        LogSelectedSourceScopeSnapshotted(logger, config.Id, job.Id, selectedSourceIds.Count);
        return true;
    }

    private async Task<ResolvedReviewer> ResolveReviewerContextAsync(
        CrawlConfigurationDto config,
        CancellationToken ct)
    {
        if (clientRegistry is null)
        {
            return new ResolvedReviewer(null, null);
        }

        var host = new ProviderHostRef(config.Provider, config.ProviderScopePath);
        var configuredTriggerReviewer = await clientRegistry.GetReviewerIdentityAsync(config.ClientId, host, ct);
        var effectiveReviewer = configuredTriggerReviewer
                                ?? await clientRegistry.GetEffectiveReviewerIdentityAsync(config.ClientId, host, ct);

        return new ResolvedReviewer(configuredTriggerReviewer, ResolveReviewerId(effectiveReviewer));
    }

    private async Task<ReviewStrategySelection> ResolveStrategySelectionAsync(Guid clientId, CancellationToken ct)
    {
        if (clientRegistry is not null)
        {
            var clientDefault = await clientRegistry.GetDefaultReviewStrategyAsync(clientId, ct);
            var clientDefaultProfileId = await clientRegistry.GetDefaultReviewPipelineProfileIdAsync(clientId, ct);
            if (clientDefault.HasValue)
            {
                EnsureStrategySelectable(clientDefault.Value, "client default");
                return new ReviewStrategySelection(
                    clientDefault.Value,
                    ReviewStrategySelectionSource.ClientDefault,
                    ReviewComparisonMode.Single,
                    ReviewPublicationMode.Publish,
                    null,
                    ResolvePipelineProfileId(clientDefault.Value, clientDefaultProfileId));
            }
        }

        return ReviewStrategySelection.Default with { PipelineProfileId = ReviewPipelineProfileCatalog.FileByFileBalancedProfileId };
    }

    private static string? ResolvePipelineProfileId(ReviewStrategy strategy, string? configuredProfileId)
    {
        if (strategy != ReviewStrategy.FileByFile)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(configuredProfileId)
            ? ReviewPipelineProfileCatalog.FileByFileBalancedProfileId
            : configuredProfileId;
    }

    private static void EnsureStrategySelectable(ReviewStrategy strategy, string source)
    {
        if (!ReviewStrategyPolicy.IsSelectable(strategy))
        {
            throw new InvalidOperationException($"{ReviewStrategyPolicy.GetDisabledSelectionMessage(strategy)} Selection source: {source}.");
        }
    }

    private static Guid? ResolveReviewerId(ReviewerIdentity? reviewer)
    {
        if (reviewer is null)
        {
            return null;
        }

        if (Guid.TryParse(reviewer.ExternalUserId, out var reviewerId))
        {
            return reviewerId;
        }

        return reviewer.Host.Provider != ScmProvider.AzureDevOps
            ? StableGuidGenerator.Create(reviewer.ExternalUserId)
            : null;
    }

    private async Task RunThreadMemoryStateMachineAsync(
        CrawlConfigurationDto config,
        AssignedCodeReviewRef pr,
        Guid? reviewerId,
        CancellationToken ct)
    {
        try
        {
            var scan = await prScanRepository!.GetAsync(
                config.ClientId,
                pr.Repository.ExternalRepositoryId,
                pr.CodeReview.Number,
                ct);

            if (scan is null)
            {
                // No review baseline yet — skip until the first review job has run and
                // written the initial scan record. Avoids spurious "first-crawl" resolved events.
                LogStateMachineSkippedNoScan(logger, pr.CodeReview.Number, config.ClientId);
                return;
            }

            var currentThreads = await threadStatusFetcher!.GetReviewerThreadStatusesAsync(
                config.ProviderScopePath,
                config.ProviderProjectKey,
                pr.Repository.ExternalRepositoryId,
                pr.CodeReview.Number,
                reviewerId ?? Guid.Empty,
                config.ClientId,
                ct);

            if (currentThreads.Count == 0)
            {
                return;
            }

            LogStateMachineEvaluating(logger, pr.CodeReview.Number, currentThreads.Count);

            // Process each thread: detect transitions and dispatch domain events.
            foreach (var thread in currentThreads)
            {
                var stored = scan.Threads.FirstOrDefault(t => t.ThreadId == thread.ThreadId);
                var previousStatus = stored?.LastSeenStatus;
                var isCurrentlyResolved = IsResolvedStatus(thread.Status);
                var wasPreviouslyResolved = IsResolvedStatus(previousStatus);

                if (isCurrentlyResolved && !wasPreviouslyResolved)
                {
                    await threadMemoryService!.HandleThreadResolvedAsync(
                        new ThreadResolvedDomainEvent(
                            config.ClientId,
                            pr.Repository.ExternalRepositoryId,
                            pr.CodeReview.Number,
                            thread.ThreadId,
                            thread.FilePath,
                            null,
                            thread.CommentHistory,
                            DateTimeOffset.UtcNow),
                        ct);
                }
                else if (!isCurrentlyResolved && wasPreviouslyResolved)
                {
                    await threadMemoryService!.HandleThreadReopenedAsync(
                        new ThreadReopenedDomainEvent(
                            config.ClientId,
                            pr.Repository.ExternalRepositoryId,
                            pr.CodeReview.Number,
                            thread.ThreadId,
                            DateTimeOffset.UtcNow),
                        ct);
                }
                else
                {
                    LogStateMachineNoOp(logger, pr.CodeReview.Number, thread.ThreadId, previousStatus, thread.Status);
                }
            }

            // Persist updated LastSeenStatus values for all current threads.
            await this.UpdateLastSeenStatusesAsync(scan, currentThreads, ct);
        }
        catch (Exception ex)
        {
            LogStateMachineFailed(logger, pr.CodeReview.Number, config.ClientId, ex);
        }
    }

    private async Task UpdateLastSeenStatusesAsync(
        ReviewPrScan existingScan,
        IReadOnlyList<PrThreadStatusEntry> currentThreads,
        CancellationToken ct)
    {
        // Build an updated scan with the new LastSeenStatus values, preserving all other fields.
        var updatedScan = new ReviewPrScan(
            existingScan.Id,
            existingScan.ClientId,
            existingScan.RepositoryId,
            existingScan.PullRequestId,
            existingScan.LastProcessedCommitId);

        foreach (var thread in currentThreads)
        {
            var existing = existingScan.Threads.FirstOrDefault(t => t.ThreadId == thread.ThreadId);
            updatedScan.Threads.Add(
                new ReviewPrScanThread
                {
                    ReviewPrScanId = existingScan.Id,
                    ThreadId = thread.ThreadId,
                    LastSeenReplyCount = existing?.LastSeenReplyCount ?? 0,
                    LastSeenStatus = thread.Status,
                });
        }

        // Preserve threads not returned by the status fetcher (only reviewer-owned threads are returned).
        foreach (var oldThread in existingScan.Threads)
        {
            if (!currentThreads.Any(t => t.ThreadId == oldThread.ThreadId))
            {
                updatedScan.Threads.Add(
                    new ReviewPrScanThread
                    {
                        ReviewPrScanId = existingScan.Id,
                        ThreadId = oldThread.ThreadId,
                        LastSeenReplyCount = oldThread.LastSeenReplyCount,
                        LastSeenStatus = oldThread.LastSeenStatus,
                    });
            }
        }

        await prScanRepository!.UpsertAsync(updatedScan, ct);
    }

    private static bool IsResolvedStatus(string? status)
    {
        return string.Equals(status, "Fixed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "Closed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "WontFix", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "ByDesign", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ResolvedReviewer(ReviewerIdentity? ConfiguredTriggerReviewer, Guid? EffectiveReviewerId);
}

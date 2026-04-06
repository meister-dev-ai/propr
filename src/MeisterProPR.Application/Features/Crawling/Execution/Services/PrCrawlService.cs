// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.Events;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Application.Services;

/// <summary>Orchestrates the periodic PR crawl: discovers assigned PRs and creates pending review jobs.</summary>
public sealed partial class PrCrawlService(
    ICrawlConfigurationRepository crawlConfigs,
    IAssignedPrFetcher prFetcher,
    IJobRepository jobs,
    IPrStatusFetcher prStatusFetcher,
    ILogger<PrCrawlService> logger,
    IReviewerThreadStatusFetcher? threadStatusFetcher = null,
    IThreadMemoryService? threadMemoryService = null,
    IReviewPrScanRepository? prScanRepository = null) : IPrCrawlService
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
            IReadOnlyList<AssignedPullRequestRef> assignedPrs;
            try
            {
                assignedPrs = await prFetcher.GetAssignedOpenPullRequestsAsync(config, cancellationToken);
                LogPrsDiscovered(logger, assignedPrs.Count, config.OrganizationUrl, config.ProjectId);
            }
            catch (Exception ex)
            {
                LogConfigFetchError(logger, config.OrganizationUrl, config.ProjectId, ex);
                continue;
            }

            foreach (var pr in assignedPrs)
            {
                if (!await this.ShouldEnqueueReviewAsync(config, pr, cancellationToken))
                {
                    continue;
                }

                var job = new ReviewJob(
                    Guid.NewGuid(),
                    config.ClientId,
                    pr.OrganizationUrl,
                    pr.ProjectId,
                    pr.RepositoryId,
                    pr.PullRequestId,
                    pr.LatestIterationId);

                if (!this.TryApplyProCursorSourceScope(config, job))
                {
                    continue;
                }

                await jobs.AddAsync(job, cancellationToken);

                // Populate PR context snapshot from data already fetched by the crawler (no second ADO call needed).
                if (pr.PrTitle is not null || pr.RepositoryName is not null)
                {
                    job.SetPrContext(pr.PrTitle, pr.RepositoryName, pr.SourceBranch, pr.TargetBranch);
                    await jobs.UpdatePrContextAsync(job.Id, pr.PrTitle, pr.RepositoryName, pr.SourceBranch, pr.TargetBranch, cancellationToken);
                }

                LogJobCreated(logger, job.Id, pr.PullRequestId, pr.LatestIterationId);
            }

            // --- Abandonment detection second pass ---
            // For any Pending/Processing job associated with this config that is no longer
            // present in the discovered-PR list, check the live ADO status. If the PR is
            // Abandoned, transition the job to Cancelled to stop further AI processing.
            var activeJobs = await jobs.GetActiveJobsForConfigAsync(
                config.OrganizationUrl, config.ProjectId, cancellationToken);
            var discoveredPrIds = new HashSet<int>(assignedPrs.Select(p => p.PullRequestId));

            foreach (var activeJob in activeJobs)
            {
                if (discoveredPrIds.Contains(activeJob.PullRequestId))
                {
                    continue;
                }

                LogAbandonmentCheckStarted(logger, activeJob.Id, activeJob.PullRequestId);

                var status = await prStatusFetcher.GetStatusAsync(
                    config.OrganizationUrl,
                    config.ProjectId,
                    activeJob.RepositoryId,
                    activeJob.PullRequestId,
                    config.ClientId,
                    cancellationToken);

                if (status == PrStatus.Abandoned)
                {
                    LogJobCancelledForAbandonedPr(logger, activeJob.PullRequestId, activeJob.Id);
                    await jobs.SetCancelledAsync(activeJob.Id, cancellationToken);
                }
            }

            // --- Thread memory state machine ---
            // Detects per-thread status transitions and dispatches domain events so that
            // ThreadMemoryService can store/remove embeddings independently of review jobs.
            if (threadStatusFetcher is null || threadMemoryService is null || prScanRepository is null)
            {
                LogStateMachineServicesUnavailable(
                    logger,
                    config.OrganizationUrl,
                    config.ProjectId,
                    threadStatusFetcher is null,
                    threadMemoryService is null,
                    prScanRepository is null);
            }
            else if (!config.ReviewerId.HasValue)
            {
                LogStateMachineSkippedNoReviewerId(logger, config.OrganizationUrl, config.ProjectId);
            }
            else
            {
                foreach (var pr in assignedPrs)
                {
                    await this.RunThreadMemoryStateMachineAsync(config, pr, cancellationToken);
                }
            }
        }
    }

    private async Task<bool> ShouldEnqueueReviewAsync(
        CrawlConfigurationDto config,
        AssignedPullRequestRef pr,
        CancellationToken ct)
    {
        var existingJob = jobs.FindActiveJob(
            pr.OrganizationUrl,
            pr.ProjectId,
            pr.RepositoryId,
            pr.PullRequestId,
            pr.LatestIterationId);

        if (existingJob is not null)
        {
            LogJobAlreadyExists(logger, pr.PullRequestId, pr.LatestIterationId, existingJob.Id);
            return false;
        }

        var completedJob = jobs.FindCompletedJob(
            pr.OrganizationUrl,
            pr.ProjectId,
            pr.RepositoryId,
            pr.PullRequestId,
            pr.LatestIterationId);
        var completedSameIterationAlreadyReviewed = completedJob is not null;

        if (prScanRepository is null || threadStatusFetcher is null || !config.ReviewerId.HasValue)
        {
            if (completedSameIterationAlreadyReviewed)
            {
                LogSkippedNoReviewChanges(logger, pr.PullRequestId, pr.LatestIterationId);
                return false;
            }

            return true;
        }

        try
        {
            var scan = await prScanRepository.GetAsync(config.ClientId, pr.RepositoryId, pr.PullRequestId, ct);
            if (scan is null)
            {
                if (completedSameIterationAlreadyReviewed)
                {
                    LogSkippedNoReviewChanges(logger, pr.PullRequestId, pr.LatestIterationId);
                    return false;
                }

                return true;
            }

            var iterationKey = pr.LatestIterationId.ToString();
            if (!string.Equals(scan.LastProcessedCommitId, iterationKey, StringComparison.Ordinal))
            {
                if (completedSameIterationAlreadyReviewed)
                {
                    LogSkippedNoReviewChanges(logger, pr.PullRequestId, pr.LatestIterationId);
                    return false;
                }

                return true;
            }

            var currentThreads = await threadStatusFetcher.GetReviewerThreadStatusesAsync(
                pr.OrganizationUrl,
                pr.ProjectId,
                pr.RepositoryId,
                pr.PullRequestId,
                config.ReviewerId.Value,
                config.ClientId,
                ct);

            if (HasNewReviewerThreadReplies(currentThreads, scan))
            {
                LogSameIterationThreadChangeDetected(logger, pr.PullRequestId, pr.LatestIterationId);
                return true;
            }

            LogSkippedNoReviewChanges(logger, pr.PullRequestId, pr.LatestIterationId);
            return false;
        }
        catch (Exception ex)
        {
            LogEnqueueDecisionFailed(logger, pr.PullRequestId, pr.LatestIterationId, ex);
            return true;
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

    private async Task RunThreadMemoryStateMachineAsync(
        CrawlConfigurationDto config,
        AssignedPullRequestRef pr,
        CancellationToken ct)
    {
        try
        {
            var scan = await prScanRepository!.GetAsync(config.ClientId, pr.RepositoryId, pr.PullRequestId, ct);

            if (scan is null)
            {
                // No review baseline yet — skip until the first review job has run and
                // written the initial scan record. Avoids spurious "first-crawl" resolved events.
                LogStateMachineSkippedNoScan(logger, pr.PullRequestId, config.ClientId);
                return;
            }

            var currentThreads = await threadStatusFetcher!.GetReviewerThreadStatusesAsync(
                config.OrganizationUrl,
                config.ProjectId,
                pr.RepositoryId,
                pr.PullRequestId,
                config.ReviewerId!.Value,
                config.ClientId,
                ct);

            if (currentThreads.Count == 0)
            {
                return;
            }

            LogStateMachineEvaluating(logger, pr.PullRequestId, currentThreads.Count);

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
                            pr.RepositoryId,
                            pr.PullRequestId,
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
                            pr.RepositoryId,
                            pr.PullRequestId,
                            thread.ThreadId,
                            DateTimeOffset.UtcNow),
                        ct);
                }
                else
                {
                    LogStateMachineNoOp(logger, pr.PullRequestId, thread.ThreadId, previousStatus, thread.Status);
                }
            }

            // Persist updated LastSeenStatus values for all current threads.
            await this.UpdateLastSeenStatusesAsync(scan, pr, currentThreads, ct);
        }
        catch (Exception ex)
        {
            LogStateMachineFailed(logger, pr.PullRequestId, config.ClientId, ex);
        }
    }

    private async Task UpdateLastSeenStatusesAsync(
        ReviewPrScan existingScan,
        AssignedPullRequestRef pr,
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
            updatedScan.Threads.Add(new ReviewPrScanThread
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
                updatedScan.Threads.Add(new ReviewPrScanThread
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
}

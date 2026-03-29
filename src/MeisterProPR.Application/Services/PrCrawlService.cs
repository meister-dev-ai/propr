using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Application.Services;

/// <summary>Orchestrates the periodic PR crawl: discovers assigned PRs and creates pending review jobs.</summary>
public sealed partial class PrCrawlService(
    ICrawlConfigurationRepository crawlConfigs,
    IAssignedPrFetcher prFetcher,
    IJobRepository jobs,
    IPrStatusFetcher prStatusFetcher,
    ILogger<PrCrawlService> logger) : IPrCrawlService
{
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
                var existingJob = jobs.FindActiveJob(
                    pr.OrganizationUrl,
                    pr.ProjectId,
                    pr.RepositoryId,
                    pr.PullRequestId,
                    pr.LatestIterationId);

                if (existingJob is not null)
                {
                    LogJobAlreadyExists(logger, pr.PullRequestId, pr.LatestIterationId, existingJob.Id);
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
        }
    }
}

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Application.Services;

/// <summary>Orchestrates the periodic PR crawl: discovers assigned PRs and creates pending review jobs.</summary>
public sealed partial class PrCrawlService(
    ICrawlConfigurationRepository crawlConfigs,
    IAssignedPrFetcher prFetcher,
    IJobRepository jobs,
    ILogger<PrCrawlService> logger) : IPrCrawlService
{
    /// <summary>
    ///     Runs one crawl cycle: loads all active crawl configurations, discovers assigned PRs,
    ///     and creates a pending <see cref="ReviewJob" /> for each unreviewed PR iteration.
    /// </summary>
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
            }
            catch (Exception ex)
            {
                LogConfigFetchError(logger, config.OrganizationUrl, config.ProjectId, ex);
                continue;
            }

            LogPrsDiscovered(logger, assignedPrs.Count, config.OrganizationUrl, config.ProjectId);

            foreach (var pr in assignedPrs)
            {
                var existing = jobs.FindActiveJob(
                    pr.OrganizationUrl,
                    pr.ProjectId,
                    pr.RepositoryId,
                    pr.PullRequestId,
                    pr.LatestIterationId);

                if (existing is not null)
                {
                    LogJobAlreadyExists(logger, pr.PullRequestId, pr.LatestIterationId, existing.Id);
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
                LogJobCreated(logger, job.Id, pr.PullRequestId, pr.LatestIterationId);
            }
        }
    }
}

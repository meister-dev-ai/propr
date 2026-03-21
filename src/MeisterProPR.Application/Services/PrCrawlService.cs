using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Application.Services;

/// <summary>Orchestrates the periodic PR crawl: discovers assigned PRs and creates pending review jobs.</summary>
public sealed partial class PrCrawlService(
    ICrawlConfigurationRepository crawlConfigs,
    IAssignedPullRequestFetcher prFetcher,
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to fetch assigned PRs for {OrgUrl}/{ProjectId}")]
    private static partial void LogConfigFetchError(ILogger logger, string orgUrl, string projectId, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "PR crawl started. Active configurations: {Count}")]
    private static partial void LogCrawlStarted(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Job already exists for PR #{PrId} iteration {IterationId}: {JobId}")]
    private static partial void LogJobAlreadyExists(ILogger logger, int prId, int iterationId, Guid jobId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Created new review job {JobId} for PR #{PrId} iteration {IterationId}")]
    private static partial void LogJobCreated(ILogger logger, Guid jobId, int prId, int iterationId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Discovered {Count} assigned PRs in {OrgUrl}/{ProjectId}")]
    private static partial void LogPrsDiscovered(ILogger logger, int count, string orgUrl, string projectId);
}

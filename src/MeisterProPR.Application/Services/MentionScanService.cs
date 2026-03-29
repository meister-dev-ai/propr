using System.Threading.Channels;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Application.Services;

/// <summary>
///     Orchestrates a single mention scan cycle across all active crawl configurations.
///     Discovers recently updated PRs, detects bot mentions in comment threads,
///     and enqueues <see cref="MentionReplyJob" /> items for processing.
/// </summary>
public sealed partial class MentionScanService(
    ICrawlConfigurationRepository crawlConfigs,
    IActivePrFetcher activePrFetcher,
    IPullRequestFetcher pullRequestFetcher,
    IMentionScanRepository scanRepository,
    IMentionReplyJobRepository jobRepository,
    ChannelWriter<MentionReplyJob> channelWriter,
    ILogger<MentionScanService> logger) : IMentionScanService
{
    // Default look-back window for the first scan when no watermark exists.
    private static readonly TimeSpan InitialLookBack = TimeSpan.FromHours(1);

    /// <inheritdoc />
    public async Task ScanAsync(CancellationToken cancellationToken = default)
    {
        var configs = await crawlConfigs.GetAllActiveAsync(cancellationToken);
        LogScanCycleStarted(logger, configs.Count);

        foreach (var config in configs)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            await this.ScanConfigAsync(config, cancellationToken);
        }
    }

    private async Task ScanConfigAsync(CrawlConfigurationDto config, CancellationToken ct)
    {
        if (config.ReviewerId is null)
        {
            LogSkippedNoReviewerId(logger, config.Id, config.ClientId);
            return;
        }

        try
        {
            var projectScan = await scanRepository.GetProjectScanAsync(config.Id, ct);
            var updatedAfter = projectScan?.LastScannedAt ?? DateTimeOffset.UtcNow.Subtract(InitialLookBack);

            var recentPrs = await activePrFetcher.GetRecentlyUpdatedPullRequestsAsync(
                config.OrganizationUrl,
                config.ProjectId,
                updatedAfter,
                config.ClientId,
                ct);

            LogPrsFound(logger, config.OrganizationUrl, config.ProjectId, recentPrs.Count);

            foreach (var pr in recentPrs)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                await this.ScanPrAsync(config, pr.RepositoryId, pr.PullRequestId, pr.LastUpdatedAt, config.ReviewerId.Value, ct);
            }

            // Advance the project-level watermark.
            var updatedProjectScan = projectScan ?? new MentionProjectScan(Guid.NewGuid(), config.Id, DateTimeOffset.UtcNow);

            updatedProjectScan.LastScannedAt = DateTimeOffset.UtcNow;
            await scanRepository.UpsertProjectScanAsync(updatedProjectScan, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogConfigScanError(logger, config.Id, ex);
        }
    }

    private async Task ScanPrAsync(
        CrawlConfigurationDto config,
        string repositoryId,
        int pullRequestId,
        DateTimeOffset prLastUpdatedAt,
        Guid reviewerGuid,
        CancellationToken ct)
    {
        var prScan = await scanRepository.GetPrScanAsync(config.Id, repositoryId, pullRequestId, ct);

        // Skip PRs that have not received new activity since the last scan.
        if (prScan is not null && prLastUpdatedAt <= prScan.LastCommentSeenAt)
        {
            LogPrSkippedNoNewActivity(logger, pullRequestId);
            return;
        }

        // Fetch the full PR with thread context (iterationId = 1 is sufficient for comment scanning).
        PullRequest pullRequest;
        try
        {
            pullRequest = await pullRequestFetcher.FetchAsync(
                config.OrganizationUrl,
                config.ProjectId,
                repositoryId,
                pullRequestId,
                1,
                null,
                config.ClientId,
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPrFetchError(logger, pullRequestId, ex);
            return;
        }

        var threads = pullRequest.ExistingThreads ?? [];
        var latestCommentTimestamp = prScan?.LastCommentSeenAt ?? DateTimeOffset.MinValue;
        var newMentionsEnqueued = 0;

        foreach (var thread in threads)
        {
            foreach (var comment in thread.Comments)
            {
                // Advance the watermark to the latest comment we've seen.
                if (comment.PublishedAt.HasValue && comment.PublishedAt.Value > latestCommentTimestamp)
                {
                    latestCommentTimestamp = comment.PublishedAt.Value;
                }

                // Skip comments without a valid ID (shouldn't happen with real ADO data).
                if (comment.CommentId <= 0)
                {
                    continue;
                }

                // Skip comments we've already processed (published before or at last seen time).
                if (comment.PublishedAt.HasValue &&
                    prScan is not null &&
                    comment.PublishedAt.Value <= prScan.LastCommentSeenAt)
                {
                    continue;
                }

                // Log the raw content so we can see what ADO actually stores (helps detect format changes).
                LogCommentContent(logger, thread.ThreadId, comment.CommentId, comment.Content);

                if (!MentionDetector.IsMentioned(comment.Content, reviewerGuid))
                {
                    continue;
                }

                // Check for duplicate — unique constraint is the authoritative guard.
                var alreadyExists = await jobRepository.ExistsForCommentAsync(config.ClientId, pullRequestId, thread.ThreadId, comment.CommentId, ct);

                if (alreadyExists)
                {
                    LogDuplicateMentionSkipped(logger, pullRequestId, thread.ThreadId, comment.CommentId);
                    continue;
                }

                var job = new MentionReplyJob(
                    Guid.NewGuid(),
                    config.ClientId,
                    config.OrganizationUrl,
                    config.ProjectId,
                    repositoryId,
                    pullRequestId,
                    thread.ThreadId,
                    comment.CommentId,
                    comment.Content);

                await jobRepository.AddAsync(job, ct);
                await channelWriter.WriteAsync(job, ct);
                newMentionsEnqueued++;
                LogMentionEnqueued(logger, pullRequestId, thread.ThreadId, comment.CommentId);
            }
        }

        // Update the PR-level watermark.
        var updatedPrScan = prScan ?? new MentionPrScan(Guid.NewGuid(), config.Id, repositoryId, pullRequestId, latestCommentTimestamp);

        if (latestCommentTimestamp > DateTimeOffset.MinValue)
        {
            updatedPrScan.LastCommentSeenAt = latestCommentTimestamp;
        }

        await scanRepository.UpsertPrScanAsync(updatedPrScan, ct);
        if (newMentionsEnqueued == 0)
        {
            LogPrScanCompletedNoMentions(logger, pullRequestId);
        }
        else
        {
            LogPrScanCompletedWithMentions(logger, pullRequestId, newMentionsEnqueued);
        }
    }
}

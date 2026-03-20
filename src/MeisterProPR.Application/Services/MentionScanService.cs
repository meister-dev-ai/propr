using System.Threading.Channels;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
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
            var updatedProjectScan = projectScan is not null
                ? projectScan
                : new MentionProjectScan(Guid.NewGuid(), config.Id, DateTimeOffset.UtcNow);
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
        MeisterProPR.Domain.ValueObjects.PullRequest pullRequest;
        try
        {
            pullRequest = await pullRequestFetcher.FetchAsync(
                config.OrganizationUrl,
                config.ProjectId,
                repositoryId,
                pullRequestId,
                iterationId: 1,
                clientId: config.ClientId,
                cancellationToken: ct);
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
                var alreadyExists = await jobRepository.ExistsForCommentAsync(
                    config.ClientId, pullRequestId, thread.ThreadId, comment.CommentId, ct);

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
        var updatedPrScan = prScan is not null
            ? prScan
            : new MentionPrScan(Guid.NewGuid(), config.Id, repositoryId, pullRequestId, latestCommentTimestamp);
        if (latestCommentTimestamp > DateTimeOffset.MinValue)
        {
            updatedPrScan.LastCommentSeenAt = latestCommentTimestamp;
        }

        await scanRepository.UpsertPrScanAsync(updatedPrScan, ct);
        LogPrScanCompleted(logger, pullRequestId, newMentionsEnqueued);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "MentionScanService: starting scan cycle across {ConfigCount} active configs")]
    private static partial void LogScanCycleStarted(ILogger logger, int configCount);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "MentionScanService: skipping config {ConfigId} for client {ClientId} — reviewer identity not configured")]
    private static partial void LogSkippedNoReviewerId(ILogger logger, Guid configId, Guid clientId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "MentionScanService: {PrCount} recently updated PRs in {OrganizationUrl}/{ProjectId}")]
    private static partial void LogPrsFound(ILogger logger, string organizationUrl, string projectId, int prCount);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "MentionScanService: evaluating comment thread={ThreadId} commentId={CommentId} content='{Content}'")]
    private static partial void LogCommentContent(ILogger logger, int threadId, int commentId, string content);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "MentionScanService: PR #{PullRequestId} skipped — no new activity since last scan")]
    private static partial void LogPrSkippedNoNewActivity(ILogger logger, int pullRequestId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "MentionScanService: failed to fetch PR #{PullRequestId}")]
    private static partial void LogPrFetchError(ILogger logger, int pullRequestId, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "MentionScanService: PR #{PullRequestId} thread {ThreadId} comment {CommentId} already has a job — skipping")]
    private static partial void LogDuplicateMentionSkipped(ILogger logger, int pullRequestId, int threadId, int commentId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "MentionScanService: enqueued mention reply job for PR #{PullRequestId} thread {ThreadId} comment {CommentId}")]
    private static partial void LogMentionEnqueued(ILogger logger, int pullRequestId, int threadId, int commentId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "MentionScanService: finished scanning PR #{PullRequestId} — {NewMentions} new mention(s) enqueued")]
    private static partial void LogPrScanCompleted(ILogger logger, int pullRequestId, int newMentions);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "MentionScanService: error scanning config {ConfigId}")]
    private static partial void LogConfigScanError(ILogger logger, Guid configId, Exception ex);
}

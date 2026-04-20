// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Threading.Channels;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
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
    IClientRegistry clientRegistry,
    IMentionScanRepository scanRepository,
    IMentionReplyJobRepository jobRepository,
    ChannelWriter<MentionReplyJob> channelWriter,
    ILogger<MentionScanService> logger,
    IProviderActivationService? providerActivationService = null) : IMentionScanService
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

            if (providerActivationService is not null &&
                !await providerActivationService.IsEnabledAsync(config.Provider, cancellationToken))
            {
                continue;
            }

            await this.ScanConfigAsync(config, cancellationToken);
        }
    }

    private async Task ScanConfigAsync(CrawlConfigurationDto config, CancellationToken ct)
    {
        if (config.ReviewerId is null)
        {
            var fallbackHost = new ProviderHostRef(config.Provider, config.ProviderScopePath);
            var fallbackReviewer = await clientRegistry.GetReviewerIdentityAsync(config.ClientId, fallbackHost, ct);
            if (fallbackReviewer is null)
            {
                LogSkippedNoReviewerId(logger, config.Id, config.ClientId);
                return;
            }
        }

        try
        {
            var reviewer = await this.ResolveReviewerIdentityAsync(config, ct);
            if (reviewer is null)
            {
                LogSkippedNoReviewerId(logger, config.Id, config.ClientId);
                return;
            }

            var projectScan = await scanRepository.GetProjectScanAsync(config.Id, ct);
            var updatedAfter = projectScan?.LastScannedAt ?? DateTimeOffset.UtcNow.Subtract(InitialLookBack);

            var recentPrs = await activePrFetcher.GetRecentlyUpdatedPullRequestsAsync(
                config.ProviderScopePath,
                config.ProviderProjectKey,
                updatedAfter,
                config.ClientId,
                ct);

            LogPrsFound(logger, config.ProviderScopePath, config.ProviderProjectKey, recentPrs.Count);

            foreach (var pr in recentPrs)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                await this.ScanPrAsync(config, reviewer, pr.RepositoryId, pr.PullRequestId, pr.LastUpdatedAt, ct);
            }

            // Advance the project-level watermark.
            var updatedProjectScan = projectScan ?? new MentionProjectScan(
                Guid.NewGuid(),
                config.Id,
                DateTimeOffset.UtcNow);

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
        ReviewerIdentity reviewer,
        string repositoryId,
        int pullRequestId,
        DateTimeOffset prLastUpdatedAt,
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
                config.ProviderScopePath,
                config.ProviderProjectKey,
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

                if (!MentionDetector.IsMentioned(comment.Content, reviewer))
                {
                    continue;
                }

                // Check for duplicate — unique constraint is the authoritative guard.
                var alreadyExists = await jobRepository.ExistsForCommentAsync(
                    config.ClientId,
                    repositoryId,
                    pullRequestId,
                    thread.ThreadId,
                    comment.CommentId,
                    ct);

                if (alreadyExists)
                {
                    LogDuplicateMentionSkipped(logger, pullRequestId, thread.ThreadId, comment.CommentId);
                    continue;
                }

                var job = new MentionReplyJob(
                    Guid.NewGuid(),
                    config.ClientId,
                    config.ProviderScopePath,
                    config.ProviderProjectKey,
                    repositoryId,
                    pullRequestId,
                    thread.ThreadId,
                    comment.CommentId,
                    comment.Content,
                    thread.FilePath,
                    thread.LineNumber,
                    comment.AuthorId,
                    comment.AuthorName,
                    comment.PublishedAt);

                var host = new ProviderHostRef(config.Provider, config.ProviderScopePath);
                var repository = new RepositoryRef(
                    host,
                    repositoryId,
                    config.ProviderProjectKey,
                    ResolveRepositoryProjectPath(config, repositoryId, pullRequest));
                var review = new CodeReviewRef(
                    repository,
                    CodeReviewPlatformKind.PullRequest,
                    pullRequestId.ToString(),
                    pullRequestId);
                var threadRef = new ReviewThreadRef(
                    review,
                    thread.ThreadId.ToString(),
                    thread.FilePath,
                    thread.LineNumber,
                    false);
                var commentAuthorExternalUserId = comment.AuthorId?.ToString("D") ?? comment.AuthorName;
                var commentRef = new ReviewCommentRef(
                    threadRef,
                    comment.CommentId.ToString(),
                    new ReviewerIdentity(
                        host,
                        commentAuthorExternalUserId ?? reviewer.ExternalUserId,
                        comment.AuthorName,
                        comment.AuthorName,
                        false),
                    comment.PublishedAt);

                job.SetProviderReviewContext(review);
                job.SetReviewThreadContext(threadRef);
                job.SetReviewCommentContext(commentRef);

                await jobRepository.AddAsync(job, ct);
                await channelWriter.WriteAsync(job, ct);
                newMentionsEnqueued++;
                LogMentionEnqueued(logger, pullRequestId, thread.ThreadId, comment.CommentId);
            }
        }

        // Update the PR-level watermark.
        var updatedPrScan = prScan ?? new MentionPrScan(
            Guid.NewGuid(),
            config.Id,
            repositoryId,
            pullRequestId,
            latestCommentTimestamp);

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

    private async Task<ReviewerIdentity?> ResolveReviewerIdentityAsync(
        CrawlConfigurationDto config,
        CancellationToken ct)
    {
        var host = new ProviderHostRef(config.Provider, config.ProviderScopePath);
        var reviewer = await clientRegistry.GetReviewerIdentityAsync(config.ClientId, host, ct);
        if (reviewer is not null)
        {
            return reviewer;
        }

        if (config.Provider != ScmProvider.AzureDevOps || !config.ReviewerId.HasValue)
        {
            return null;
        }

        var reviewerId = config.ReviewerId.Value.ToString("D");
        return new ReviewerIdentity(host, reviewerId, reviewerId, reviewerId, false);
    }

    private static string ResolveRepositoryProjectPath(
        CrawlConfigurationDto config,
        string repositoryId,
        PullRequest pullRequest)
    {
        if (config.Provider == ScmProvider.AzureDevOps)
        {
            return config.ProviderProjectKey;
        }

        if (!string.IsNullOrWhiteSpace(pullRequest.RepositoryName) &&
            pullRequest.RepositoryName.Contains('/', StringComparison.Ordinal))
        {
            return pullRequest.RepositoryName;
        }

        return string.IsNullOrWhiteSpace(config.ProviderProjectKey)
            ? repositoryId
            : $"{config.ProviderProjectKey.TrimEnd('/')}/{repositoryId}";
    }
}

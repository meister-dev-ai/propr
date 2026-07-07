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
        var latestCommentTimestamp = ComputeLatestCommentTimestamp(
            threads,
            prScan?.LastCommentSeenAt ?? DateTimeOffset.MinValue);
        var newMentionsEnqueued = 0;

        foreach (var thread in threads)
        {
            foreach (var comment in thread.Comments)
            {
                if (await this.ProcessCommentForMentionAsync(
                        new MentionCommentInputs(
                            config,
                            reviewer,
                            repositoryId,
                            pullRequestId,
                            pullRequest,
                            thread,
                            comment,
                            prScan,
                            ct)))
                {
                    newMentionsEnqueued++;
                }
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

    private static DateTimeOffset ComputeLatestCommentTimestamp(
        IReadOnlyList<PrCommentThread> threads,
        DateTimeOffset seed)
    {
        // Advance the watermark to the latest comment we've seen, but never below the previous watermark:
        // the seed is always in the running set, so the result can only move forward (monotonic).
        return threads
            .SelectMany(thread => thread.Comments)
            .Select(comment => comment.PublishedAt)
            .Where(publishedAt => publishedAt.HasValue)
            .Select(publishedAt => publishedAt!.Value)
            .Append(seed)
            .Max();
    }

    private static bool ShouldProcessComment(PrThreadComment comment, MentionPrScan? prScan)
    {
        // Skip comments without a valid ID (shouldn't happen with real ADO data).
        if (comment.CommentId <= 0)
        {
            return false;
        }

        // Skip comments we've already processed (published before or at last seen time).
        return !comment.PublishedAt.HasValue ||
               prScan is null ||
               comment.PublishedAt.Value > prScan.LastCommentSeenAt;
    }

    // Produce a redacted, single-line, length-bounded rendering of (attacker-controlled) comment text
    // so Trace diagnostics can still spot format changes without leaking full content or letting a
    // crafted comment inject extra log lines via embedded control characters.
    private static string SanitizeCommentForLog(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        const int maxLoggedChars = 256;
        var trimmed = content.Length > maxLoggedChars ? content[..maxLoggedChars] + "…" : content;
        return new string(trimmed.Select(static ch => char.IsControl(ch) ? ' ' : ch).ToArray());
    }

    private async Task<bool> ProcessCommentForMentionAsync(MentionCommentInputs inputs)
    {
        if (!ShouldProcessComment(inputs.Comment, inputs.PrScan))
        {
            return false;
        }

        // Log a redacted, single-line, length-bounded rendering of the content so we can still detect
        // format changes without leaking full (attacker-controlled) comment text or allowing log injection.
        LogCommentContent(logger, inputs.Thread.ThreadId, inputs.Comment.CommentId, SanitizeCommentForLog(inputs.Comment.Content));

        if (!MentionDetector.IsMentioned(inputs.Comment.Content, inputs.Reviewer))
        {
            return false;
        }

        // Check for duplicate — unique constraint is the authoritative guard.
        var alreadyExists = await jobRepository.ExistsForCommentAsync(
            inputs.Config.ClientId,
            inputs.RepositoryId,
            inputs.PullRequestId,
            inputs.Thread.ThreadId,
            inputs.Comment.CommentId,
            inputs.Ct);

        if (alreadyExists)
        {
            LogDuplicateMentionSkipped(logger, inputs.PullRequestId, inputs.Thread.ThreadId, inputs.Comment.CommentId);
            return false;
        }

        var job = new MentionReplyJob(
            Guid.NewGuid(),
            inputs.Config.ClientId,
            inputs.Config.ProviderScopePath,
            inputs.Config.ProviderProjectKey,
            inputs.RepositoryId,
            inputs.PullRequestId,
            inputs.Thread.ThreadId,
            inputs.Comment.CommentId,
            inputs.Comment.Content,
            inputs.Thread.FilePath,
            inputs.Thread.LineNumber,
            inputs.Comment.AuthorId,
            inputs.Comment.AuthorName,
            inputs.Comment.PublishedAt);

        var host = new ProviderHostRef(inputs.Config.Provider, inputs.Config.ProviderScopePath);
        var repository = new RepositoryRef(
            host,
            inputs.RepositoryId,
            inputs.Config.ProviderProjectKey,
            ResolveRepositoryProjectPath(inputs.Config, inputs.RepositoryId, inputs.PullRequest));
        var review = new CodeReviewRef(
            repository,
            CodeReviewPlatformKind.PullRequest,
            inputs.PullRequestId.ToString(),
            inputs.PullRequestId);
        var threadRef = new ReviewThreadRef(
            review,
            inputs.Thread.ThreadId.ToString(),
            inputs.Thread.FilePath,
            inputs.Thread.LineNumber,
            false);
        var commentAuthorExternalUserId = inputs.Comment.AuthorId?.ToString("D") ?? inputs.Comment.AuthorName;
        var commentRef = new ReviewCommentRef(
            threadRef,
            inputs.Comment.CommentId.ToString(),
            new ReviewerIdentity(
                host,
                commentAuthorExternalUserId ?? inputs.Reviewer.ExternalUserId,
                inputs.Comment.AuthorName,
                inputs.Comment.AuthorName,
                false),
            inputs.Comment.PublishedAt);

        job.SetProviderReviewContext(review);
        job.SetReviewThreadContext(threadRef);
        job.SetReviewCommentContext(commentRef);

        await jobRepository.AddAsync(job, inputs.Ct);
        await channelWriter.WriteAsync(job, inputs.Ct);
        LogMentionEnqueued(logger, inputs.PullRequestId, inputs.Thread.ThreadId, inputs.Comment.CommentId);
        return true;
    }

    private async Task<ReviewerIdentity?> ResolveReviewerIdentityAsync(
        CrawlConfigurationDto config,
        CancellationToken ct)
    {
        var host = new ProviderHostRef(config.Provider, config.ProviderScopePath);
        return await clientRegistry.GetEffectiveReviewerIdentityAsync(config.ClientId, host, ct);
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

    private sealed record MentionCommentInputs(
        CrawlConfigurationDto Config,
        ReviewerIdentity Reviewer,
        string RepositoryId,
        int PullRequestId,
        PullRequest PullRequest,
        PrCommentThread Thread,
        PrThreadComment Comment,
        MentionPrScan? PrScan,
        CancellationToken Ct);
}

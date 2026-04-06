// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.Logging;

namespace MeisterProPR.Application.Services;

public sealed partial class MentionScanService
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "MentionScanService: starting scan cycle across {ConfigCount} active configs")]
    private static partial void LogScanCycleStarted(ILogger logger, int configCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "MentionScanService: skipping config {ConfigId} for client {ClientId} — reviewer identity not configured")]
    private static partial void LogSkippedNoReviewerId(ILogger logger, Guid configId, Guid clientId);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "MentionScanService: {PrCount} recently updated PRs in {OrganizationUrl}/{ProjectId}")]
    private static partial void LogPrsFound(ILogger logger, string organizationUrl, string projectId, int prCount);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "MentionScanService: evaluating comment thread={ThreadId} commentId={CommentId} content='{Content}'")]
    private static partial void LogCommentContent(ILogger logger, int threadId, int commentId, string content);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "MentionScanService: PR #{PullRequestId} skipped — no new activity since last scan")]
    private static partial void LogPrSkippedNoNewActivity(ILogger logger, int pullRequestId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "MentionScanService: failed to fetch PR #{PullRequestId}")]
    private static partial void LogPrFetchError(ILogger logger, int pullRequestId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "MentionScanService: PR #{PullRequestId} thread {ThreadId} comment {CommentId} already has a job — skipping")]
    private static partial void LogDuplicateMentionSkipped(ILogger logger, int pullRequestId, int threadId, int commentId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "MentionScanService: enqueued mention reply job for PR #{PullRequestId} thread {ThreadId} comment {CommentId}")]
    private static partial void LogMentionEnqueued(ILogger logger, int pullRequestId, int threadId, int commentId);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "MentionScanService: finished scanning PR #{PullRequestId} — no new mentions")]
    private static partial void LogPrScanCompletedNoMentions(ILogger logger, int pullRequestId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "MentionScanService: finished scanning PR #{PullRequestId} — {NewMentions} new mention(s) enqueued")]
    private static partial void LogPrScanCompletedWithMentions(ILogger logger, int pullRequestId, int newMentions);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "MentionScanService: error scanning config {ConfigId}")]
    private static partial void LogConfigScanError(ILogger logger, Guid configId, Exception ex);
}

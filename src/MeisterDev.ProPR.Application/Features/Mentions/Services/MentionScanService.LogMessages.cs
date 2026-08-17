// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Application.Services;

public sealed partial class MentionScanService
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "MentionScanService: starting scan cycle across {ConfigCount} active configs")]
    private static partial void LogScanCycleStarted(ILogger logger, int configCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "MentionScanService: skipping config {ConfigId} for client {ClientId} — reviewer identity not configured")]
    private static partial void LogSkippedNoReviewerId(ILogger logger, Guid configId, Guid clientId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message =
            "MentionScanService: config {ConfigId} skipped — {Provider} is disabled for this installation")]
    private static partial void LogConfigSkippedProviderDisabled(
        ILogger logger,
        Guid configId,
        ScmProvider provider);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message =
            "MentionScanService: config {ConfigId} not due — last scanned at {LastScannedAt}, asking to be scanned every {ScanIntervalSeconds}s")]
    private static partial void LogConfigNotDueYet(
        ILogger logger,
        Guid configId,
        DateTimeOffset lastScannedAt,
        int scanIntervalSeconds);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "MentionScanService: {PrCount} recently updated PRs in {OrganizationUrl}/{ProjectId}")]
    private static partial void LogPrsFound(ILogger logger, string organizationUrl, string projectId, int prCount);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "MentionScanService: evaluating comment thread={ThreadId} commentId={CommentId} content='{Content}'")]
    private static partial void LogCommentContent(ILogger logger, string threadId, long commentId, string content);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "MentionScanService: PR #{PullRequestId} skipped — no new activity since last scan")]
    private static partial void LogPrSkippedNoNewActivity(ILogger logger, int pullRequestId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "MentionScanService: failed to fetch PR #{PullRequestId}")]
    private static partial void LogPrFetchError(ILogger logger, int pullRequestId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "MentionScanService: failed to read the conversation of PR #{PullRequestId}; its review threads were still scanned and its watermark is held so the conversation is read again")]
    private static partial void LogConversationFetchError(ILogger logger, int pullRequestId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message =
            "MentionScanService: configuration {ConfigId} did not read everything it claims, so its discovery window stays open from {UpdatedAfter} and the next scan asks about it again")]
    private static partial void LogScanWindowHeldOpen(
        ILogger logger,
        Guid configId,
        DateTimeOffset updatedAfter);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "MentionScanService: PR #{PullRequestId} comment {CommentId} is an answer ProPR posted — not a question")]
    private static partial void LogOwnAnswerSkipped(ILogger logger, int pullRequestId, long commentId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "MentionScanService: PR #{PullRequestId} comment {CommentId} mentions the reviewer, but {Provider} names no thread for it and replying there needs one, so it cannot be answered")]
    private static partial void LogMentionWithoutAnswerableThread(
        ILogger logger,
        int pullRequestId,
        long commentId,
        ScmProvider provider);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message =
            "MentionScanService: PR #{PullRequestId} comment {CommentId} mentions the reviewer but was published at {PublishedAt} — not after {SeenUpTo}, the later of when the repository was claimed and when it was last scanned, so it is not answered")]
    private static partial void LogMentionOlderThanFloor(
        ILogger logger,
        int pullRequestId,
        long commentId,
        DateTimeOffset? publishedAt,
        DateTimeOffset seenUpTo);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message =
            "MentionScanService: PR #{PullRequestId} thread {ThreadId} comment {CommentId} already has a job — skipping")]
    private static partial void LogDuplicateMentionSkipped(
        ILogger logger,
        int pullRequestId,
        string threadId,
        long commentId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message =
            "MentionScanService: enqueued mention reply job for PR #{PullRequestId} thread {ThreadId} comment {CommentId}")]
    private static partial void LogMentionEnqueued(ILogger logger, int pullRequestId, string threadId, long commentId);

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

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message =
            "MentionScanService: config {ConfigId} answers on {CoveredCount} of the {TotalCount} recently updated PRs in the project")]
    private static partial void LogPrsAfterRepositoryFilter(
        ILogger logger,
        Guid configId,
        int coveredCount,
        int totalCount);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message =
            "MentionScanService: PR #{PullRequestId} thread {ThreadId} comment {CommentId} was taken by another client covering the same repository")]
    private static partial void LogMentionTakenByAnotherClient(
        ILogger logger,
        int pullRequestId,
        string threadId,
        long commentId);
}

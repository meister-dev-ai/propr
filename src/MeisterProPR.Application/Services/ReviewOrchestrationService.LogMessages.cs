// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Application.Services;

public sealed partial class ReviewOrchestrationService
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Review job {JobId} failed partially ({FailedCount}/{TotalCount} files failed) — re-queuing for retry")]
    private static partial void LogPartialReviewFailure(ILogger logger, Guid jobId, int failedCount, int totalCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reviewer identity not configured for client {ClientId} — failing job {JobId}")]
    private static partial void LogReviewerIdentityMissing(ILogger logger, Guid clientId, Guid jobId);

    [LoggerMessage(Level = LogLevel.Error, Message = "No active AI connection configured for client {ClientId} — failing job {JobId}")]
    private static partial void LogNoAiConnectionConfigured(ILogger logger, Guid clientId, Guid jobId);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Starting review for job {JobId} PR#{PrId}")]
    private static partial void LogReviewStarted(ILogger logger, Guid jobId, int prId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "PR #{PrId} is no longer active (status: {Status}) — failing job {JobId}")]
    private static partial void LogPrNoLongerActive(ILogger logger, int prId, PrStatus status, Guid jobId);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Skipping review for job {JobId} PR#{PrId} — no new commits or replies")]
    private static partial void LogSkippedNoChange(ILogger logger, Guid jobId, int prId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipping review for job {JobId} PR#{PrId} — AI returned empty review")]
    private static partial void LogSkippedEmptyReview(ILogger logger, Guid jobId, int prId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Completed review for job {JobId}")]
    private static partial void LogReviewCompleted(ILogger logger, Guid jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Thread {ThreadId} on PR#{PrId} marked as fixed")]
    private static partial void LogThreadResolved(ILogger logger, int threadId, int prId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Thread {ThreadId} evaluation failed on PR#{PrId} — skipping")]
    private static partial void LogThreadEvaluationFailed(ILogger logger, int threadId, int prId, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to save ReviewPrScan for job {JobId} — processing continues")]
    private static partial void LogScanSaveFailed(ILogger logger, Guid jobId, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to begin protocol recording for job {JobId} — review continues without tracing")]
    private static partial void LogProtocolBeginFailed(ILogger logger, Guid jobId, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Review failed for job {JobId}")]
    private static partial void LogReviewFailed(ILogger logger, Guid jobId, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "PR #{PrId} was abandoned — cancelling review job {JobId} instead of failing it")]
    private static partial void LogPrAbandonedCancellingJob(ILogger logger, int prId, Guid jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Review job {JobId} was cancelled externally before file review — exiting without AI calls")]
    private static partial void LogJobCancelledBeforeFileReview(ILogger logger, Guid jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Review job {JobId} was cancelled externally after file review — discarding result; no comment posted")]
    private static partial void LogJobCancelledAfterFileReview(ILogger logger, Guid jobId);
}

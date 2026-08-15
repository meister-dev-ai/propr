// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Application.Services;

public sealed partial class MentionReplyService
{
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "MentionReplyService: job {JobId} was already claimed by another worker — skipping")]
    private static partial void LogJobAlreadyClaimed(ILogger logger, Guid jobId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "MentionReplyService: job {JobId} completed successfully")]
    private static partial void LogJobCompleted(ILogger logger, Guid jobId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "MentionReplyService: job {JobId} failed")]
    private static partial void LogJobFailed(ILogger logger, Guid jobId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "MentionReplyService: recording posted-comment provenance failed for job {JobId} — the reply is unaffected")]
    private static partial void LogPostedCommentOriginRecordingFailed(ILogger logger, Guid jobId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "MentionReplyService: job {JobId} answered nothing: the {BudgetScope} budget has spent {SpentUsd} of {ThresholdUsd} USD")]
    private static partial void LogAnswerHeldByBudget(
        ILogger logger,
        Guid jobId,
        BudgetScopeKind budgetScope,
        decimal thresholdUsd,
        decimal spentUsd);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "MentionReplyService: job {JobId} could not read the latest revision; its spend counts against the whole pull request")]
    private static partial void LogLatestRevisionLookupFailed(ILogger logger, Guid jobId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "MentionReplyService: opening the trace record for job {JobId} failed; the answer is unaffected")]
    private static partial void LogProtocolBeginFailed(ILogger logger, Guid jobId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "MentionReplyService: recording what trace record {ProtocolId} spent failed; the tokens are spent but uncounted")]
    private static partial void LogSpendRecordingFailed(ILogger logger, Guid protocolId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "MentionReplyService: job {JobId} could not tell the developer its budget is exhausted")]
    private static partial void LogBudgetNoticeNotPosted(ILogger logger, Guid jobId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "MentionReplyService: job {JobId} reached a budget cap but the block could not be recorded; it stays claimed and is retried")]
    private static partial void LogBudgetBlockRecordingFailed(ILogger logger, Guid jobId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message =
            "MentionReplyService: job {JobId} is answered past the {BudgetScope} soft cap, which has spent {SpentUsd} of {ThresholdUsd} USD")]
    private static partial void LogAnswerPastSoftCap(
        ILogger logger,
        Guid jobId,
        BudgetScopeKind budgetScope,
        decimal thresholdUsd,
        decimal spentUsd);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "MentionReplyService: job {JobId} recorded its budget block but the budget event was not published")]
    private static partial void LogBudgetEventPublishFailed(ILogger logger, Guid jobId, Exception ex);
}

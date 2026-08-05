// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

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
}

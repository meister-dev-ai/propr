using Microsoft.Extensions.Logging;

namespace MeisterProPR.Application.Services;

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
}

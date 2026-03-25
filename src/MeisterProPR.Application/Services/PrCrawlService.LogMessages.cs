using Microsoft.Extensions.Logging;

namespace MeisterProPR.Application.Services;

public sealed partial class PrCrawlService
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to fetch assigned PRs for {OrgUrl}/{ProjectId}")]
    private static partial void LogConfigFetchError(ILogger logger, string orgUrl, string projectId, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "PR crawl started. Active configurations: {Count}")]
    private static partial void LogCrawlStarted(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Job already exists for PR #{PrId} iteration {IterationId}: {JobId}")]
    private static partial void LogJobAlreadyExists(ILogger logger, int prId, int iterationId, Guid jobId);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Created new review job {JobId} for PR #{PrId} iteration {IterationId}")]
    private static partial void LogJobCreated(ILogger logger, Guid jobId, int prId, int iterationId);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Discovered {Count} assigned PRs in {OrgUrl}/{ProjectId}")]
    private static partial void LogPrsDiscovered(ILogger logger, int count, string orgUrl, string projectId);
}

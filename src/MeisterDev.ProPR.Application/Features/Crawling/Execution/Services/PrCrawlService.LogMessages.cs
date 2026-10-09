// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Application.Services;

public sealed partial class PrCrawlService
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Review target admission failed for configuration {ConfigurationId}")]
    private static partial void LogTargetAdmissionError(ILogger logger, Guid configurationId, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to fetch assigned PRs for {OrgUrl}/{ProjectId}")]
    private static partial void LogConfigFetchError(ILogger logger, string orgUrl, string projectId, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "PR crawl started. Active configurations: {Count}")]
    private static partial void LogCrawlStarted(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "Shared pull-request synchronization failed for PR #{PrId} in {OrgUrl}/{ProjectId} during {SummaryLabel}")]
    private static partial void LogSynchronizationFailed(
        ILogger logger,
        int prId,
        string orgUrl,
        string projectId,
        string summaryLabel,
        Exception ex);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Discovered {Count} assigned PRs in {OrgUrl}/{ProjectId}")]
    private static partial void LogPrsDiscovered(ILogger logger, int count, string orgUrl, string projectId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message =
            "Abandonment check: active job {JobId} for PR #{PrId} is not in discovered list — fetching live status")]
    private static partial void LogAbandonmentCheckStarted(ILogger logger, Guid jobId, int prId);
}

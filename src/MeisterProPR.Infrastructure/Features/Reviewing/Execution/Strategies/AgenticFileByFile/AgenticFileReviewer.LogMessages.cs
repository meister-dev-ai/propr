// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.AgenticFileByFile;

internal sealed partial class AgenticFileReviewer
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Starting review for file {FilePath} ({Index}/{Total}) in job {JobId}")]
    private static partial void LogFileReviewStarted(ILogger logger, string filePath, int index, int total, Guid jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Completed review for file {FilePath} in job {JobId}")]
    private static partial void LogFileReviewCompleted(ILogger logger, string filePath, Guid jobId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to begin protocol recording for file {FilePath} in job {JobId}")]
    private static partial void LogProtocolBeginFailed(ILogger logger, string filePath, Guid jobId, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "File {FilePath} classified as tier {Tier} ({ChangedLines} changed lines) in job {JobId}")]
    private static partial void LogTierAssigned(ILogger logger, string filePath, FileComplexityTier tier, int changedLines, Guid jobId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "MaxIterationsOverride={MaxIterationsOverride} applied for file {FilePath} in job {JobId}")]
    private static partial void LogMaxIterationsOverrideApplied(ILogger logger, int maxIterationsOverride, string filePath, Guid jobId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{DismissalCount} dismissal pattern(s) injected into context for file {FilePath} in job {JobId}")]
    private static partial void LogDismissalsInjected(ILogger logger, int dismissalCount, string filePath, Guid jobId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Dropped {DroppedCount} speculative comment(s) from {FilePath} for job {JobId}")]
    private static partial void LogSpeculativeCommentsDropped(ILogger logger, int droppedCount, string filePath, Guid jobId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Dropped {DroppedCount} INFO comment(s) from {FilePath} for job {JobId}")]
    private static partial void LogInfoCommentsDropped(ILogger logger, int droppedCount, string filePath, Guid jobId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Dropped {DroppedCount} vague suggestion(s) from {FilePath} for job {JobId}")]
    private static partial void LogVagueSuggestionsDropped(ILogger logger, int droppedCount, string filePath, Guid jobId);

    [LoggerMessage(
        Level = LogLevel.Debug, Message = "Downgraded {DowngradedCount} comment severity(ies) in {FilePath} for job {JobId} (confidence floor applied)")]
    private static partial void LogSeverityDowngraded(ILogger logger, int downgradedCount, string filePath, Guid jobId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agentic file review {Stage} stage fell back for file {FilePath} in job {JobId}: {Reason}")]
    private static partial void LogAgenticStageFallback(ILogger logger, string stage, string filePath, Guid jobId, string reason);
}

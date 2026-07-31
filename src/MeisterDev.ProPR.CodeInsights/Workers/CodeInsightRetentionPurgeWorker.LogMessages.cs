// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.CodeInsights.Workers;

public sealed partial class CodeInsightRetentionPurgeWorker
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "CodeInsightRetentionPurgeWorker started (interval: {IntervalSeconds:F0}s)")]
    private static partial void LogWorkerStarted(ILogger logger, double intervalSeconds);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "CodeInsightRetentionPurgeWorker stopped")]
    private static partial void LogWorkerStopped(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "CodeInsightRetentionPurgeWorker sweep completed (pull requests removed: {RemovedCount})")]
    private static partial void LogSweepCompleted(ILogger logger, int removedCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "CodeInsightRetentionPurgeWorker: code-insight store not registered: sweep skipped")]
    private static partial void LogDependenciesUnavailable(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "CodeInsightRetentionPurgeWorker: sweep cycle failed")]
    private static partial void LogSweepFailed(ILogger logger, Exception ex);
}

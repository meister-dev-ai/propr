// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.CodeInsights.Workers;

public sealed partial class CodeInsightConditionWorker
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "CodeInsightConditionWorker started (interval: {IntervalSeconds:F0}s)")]
    private static partial void LogWorkerStarted(ILogger logger, double intervalSeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "CodeInsightConditionWorker stopped")]
    private static partial void LogWorkerStopped(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "CodeInsightConditionWorker cycle completed (transitions recorded: {RecordedCount})")]
    private static partial void LogCycleCompleted(ILogger logger, int recordedCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "CodeInsightConditionWorker: the code-insight module is not registered: evaluation skipped")]
    private static partial void LogDependenciesUnavailable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "CodeInsightConditionWorker: evaluation cycle failed")]
    private static partial void LogCycleFailed(ILogger logger, Exception ex);
}

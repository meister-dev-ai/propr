// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.CodeInsights.Workers;

public sealed partial class CodeInsightClassificationWorker
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "CodeInsightClassificationWorker started (interval: {IntervalSeconds:F0}s)")]
    private static partial void LogWorkerStarted(ILogger logger, double intervalSeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "CodeInsightClassificationWorker stopped")]
    private static partial void LogWorkerStopped(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "CodeInsightClassificationWorker sweep: considered {Considered}, classified {Classified}, "
                  + "failed {Failed}, skipped by gate {SkippedByGate}, backlog remaining {BacklogRemaining}")]
    private static partial void LogSweepCompleted(
        ILogger logger,
        int considered,
        int classified,
        int failed,
        int skippedByGate,
        int backlogRemaining);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "CodeInsightClassificationWorker: the classification sweeper is not registered: sweep skipped")]
    private static partial void LogDependenciesUnavailable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "CodeInsightClassificationWorker: sweep cycle failed")]
    private static partial void LogSweepFailed(ILogger logger, Exception ex);
}

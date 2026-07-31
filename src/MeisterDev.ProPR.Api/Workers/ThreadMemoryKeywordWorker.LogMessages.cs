// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Api.Workers;

public sealed partial class ThreadMemoryKeywordWorker
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "ThreadMemoryKeywordWorker started (interval: {IntervalSeconds}s)")]
    private static partial void LogWorkerStarted(ILogger logger, double intervalSeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "ThreadMemoryKeywordWorker stopped")]
    private static partial void LogWorkerStopped(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "The thread-memory keyword sweeper is not registered; the back-fill is skipped.")]
    private static partial void LogSweeperUnavailable(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "ThreadMemoryKeywordWorker sweep completed (memories enriched: {EnrichedCount})")]
    private static partial void LogSweepCompleted(ILogger logger, int enrichedCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "A thread-memory keyword sweep failed.")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}

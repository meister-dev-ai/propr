// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.Logging;

namespace MeisterProPR.Api.Workers;

public sealed partial class ProCursorTokenUsageRollupWorker
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "ProCursorTokenUsageRollupWorker started (interval: {IntervalSeconds}s)")]
    private static partial void LogWorkerStarted(ILogger logger, int intervalSeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "ProCursorTokenUsageRollupWorker stopped")]
    private static partial void LogWorkerStopped(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "ProCursor token usage rollup cycle completed with {RefreshedBucketCount} refreshed buckets")]
    private static partial void LogCycleCompleted(ILogger logger, int refreshedBucketCount);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message =
            "ProCursor token usage retention completed (events deleted: {DeletedEventCount}, rollups deleted: {DeletedRollupCount})")]
    private static partial void LogRetentionCompleted(ILogger logger, int deletedEventCount, int deletedRollupCount);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "ProCursorTokenUsageRollupWorker: unhandled exception in rollup cycle - worker continues")]
    private static partial void LogCycleError(ILogger logger, Exception ex);
}

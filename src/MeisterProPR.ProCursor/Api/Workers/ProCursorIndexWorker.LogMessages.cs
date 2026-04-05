// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.Logging;

namespace MeisterProPR.Api.Workers;

public sealed partial class ProCursorIndexWorker
{
    [LoggerMessage(Level = LogLevel.Information, Message = "ProCursorIndexWorker started (interval: {IntervalSeconds}s)")]
    private static partial void LogWorkerStarted(ILogger logger, int intervalSeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "ProCursorIndexWorker stopped")]
    private static partial void LogWorkerStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "ProCursorIndexWorker: unhandled exception in index cycle - worker continues")]
    private static partial void LogCycleError(ILogger logger, Exception ex);
}

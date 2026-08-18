// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Api.Workers;

/// <summary>Log messages for the anonymous usage statistics send loop.</summary>
public sealed partial class UsageStatisticsSendWorker
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Anonymous usage statistics send loop started.")]
    private static partial void LogWorkerStarted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Anonymous usage statistics send loop stopped.")]
    private static partial void LogWorkerStopped(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "An anonymous usage statistics cycle failed; the next cycle builds a fresh snapshot.")]
    private static partial void LogCycleFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "The anonymous usage statistics schedule could not be read; falling back to the daily cadence.")]
    private static partial void LogScheduleUnavailable(ILogger logger, Exception exception);
}

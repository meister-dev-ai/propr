// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.ProCursor.Workers;

public sealed partial class ProCursorIndexWorker
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "ProCursorIndexWorker started (interval: {IntervalSeconds}s)")]
    private static partial void LogWorkerStarted(ILogger logger, int intervalSeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "ProCursorIndexWorker stopped")]
    private static partial void LogWorkerStopped(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "ProCursorIndexWorker skipped {ServiceName} because the ProCursor service graph is incomplete")]
    private static partial void LogServiceGraphUnavailable(ILogger logger, string serviceName, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "ProCursorIndexWorker: unhandled exception in index cycle - worker continues")]
    private static partial void LogCycleError(ILogger logger, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "ProCursor index job {JobId} was stopped because it was cancelled or recovered by another service.")]
    private static partial void LogJobStoppedWithoutLease(ILogger logger, Guid jobId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "ProCursor index job {JobId} ended with an error that is not recorded on the job. A worker recovers the job when its lease expires.")]
    private static partial void LogJobEndUnrecorded(ILogger logger, Guid jobId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The lease on ProCursor index job {JobId} could not be renewed. The next renewal tries again.")]
    private static partial void LogLeaseRenewalFailed(ILogger logger, Guid jobId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message =
            "ProCursor index job {JobId} was stopped because its lease could not be renewed before it expires. A worker recovers the job when the lease has expired.")]
    private static partial void LogLeaseUnrenewable(ILogger logger, Guid jobId);
}

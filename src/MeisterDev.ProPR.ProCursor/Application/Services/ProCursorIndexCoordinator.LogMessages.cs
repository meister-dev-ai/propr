// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.ProCursor.Core;

public sealed partial class ProCursorIndexCoordinator
{
    [LoggerMessage(Level = LogLevel.Error, Message = "ProCursor index job {JobId} failed.")]
    private static partial void LogJobFailed(ILogger logger, Guid jobId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "The completion of ProCursor index job {JobId} could not be recorded. A worker recovers the job when its lease expires.")]
    private static partial void LogCompletionUnrecorded(ILogger logger, Guid jobId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The lease on ProCursor index job {JobId} expired during attempt {AttemptCount}. The job is now {Status}.")]
    private static partial void LogExpiredJobRecovered(
        ILogger logger,
        Guid jobId,
        int attemptCount,
        ProCursorIndexJobStatus status);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "ProCursor index job {JobId} was cancelled or recovered while it ran, so its end is not recorded.")]
    private static partial void LogJobChangedWhileRunning(ILogger logger, Guid jobId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message =
            "Queued ProCursor {JobKind} job {JobId} for source {SourceId} branch {TrackedBranchId} ({BranchName})")]
    private static partial void LogRefreshQueued(
        ILogger logger,
        Guid sourceId,
        Guid trackedBranchId,
        string branchName,
        Guid jobId,
        string jobKind);
}

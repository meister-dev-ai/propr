// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.Logging;

namespace MeisterProPR.Application.Services;

public sealed partial class ProCursorIndexCoordinator
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "ProCursor index cycle heartbeat; max concurrency {MaxIndexConcurrency}.")]
    private static partial void LogIndexCycleHeartbeat(ILogger logger, int maxIndexConcurrency);

    [LoggerMessage(Level = LogLevel.Error, Message = "ProCursor index job {JobId} failed.")]
    private static partial void LogJobFailed(ILogger logger, Guid jobId, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Queued ProCursor {JobKind} job {JobId} for source {SourceId} branch {TrackedBranchId} ({BranchName})")]
    private static partial void LogRefreshQueued(ILogger logger, Guid sourceId, Guid trackedBranchId, string branchName, Guid jobId, string jobKind);
}

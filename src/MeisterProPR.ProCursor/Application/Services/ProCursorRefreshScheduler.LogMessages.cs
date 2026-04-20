// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.Logging;

namespace MeisterProPR.Application.Services;

public sealed partial class ProCursorRefreshScheduler
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "Failed to poll ProCursor source {SourceId} branch {TrackedBranchId} ({BranchName}) for refresh scheduling.")]
    private static partial void LogRefreshPollFailed(
        ILogger logger,
        Guid sourceId,
        Guid trackedBranchId,
        string branchName,
        Exception ex);
}

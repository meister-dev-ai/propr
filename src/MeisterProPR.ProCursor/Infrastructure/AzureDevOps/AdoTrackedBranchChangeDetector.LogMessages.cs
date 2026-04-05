// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.AzureDevOps.ProCursor;

public sealed partial class AdoTrackedBranchChangeDetector
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to resolve ProCursor branch head for source {SourceId} branch {TrackedBranchId} ({BranchName}).")]
    private static partial void LogBranchHeadResolutionFailed(ILogger logger, Guid sourceId, Guid trackedBranchId, string branchName, Exception ex);
}

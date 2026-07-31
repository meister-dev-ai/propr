// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.CodeInsights.Rollups;

public sealed partial class CodeInsightRollupProjector
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Projecting code-insight roll-ups for job {JobId} failed; the next touch recomputes them.")]
    private static partial void LogProjectionFailed(ILogger logger, Guid jobId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Back-filled code-insight roll-ups for {JobCount} previously unprojected job(s).")]
    private static partial void LogBackfillProgressed(ILogger logger, int jobCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Selecting code-insight roll-up backfill candidates failed; the next sweep retries.")]
    private static partial void LogBackfillFailed(ILogger logger, Exception ex);
}

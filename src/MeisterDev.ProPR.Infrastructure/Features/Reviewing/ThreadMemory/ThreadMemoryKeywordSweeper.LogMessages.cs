// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.ThreadMemory;

public sealed partial class ThreadMemoryKeywordSweeper
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Extracted search keywords for {MemoryCount} resolution memory record(s) that had none.")]
    private static partial void LogSweepProgressed(ILogger logger, int memoryCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The resolution-memory keyword backfill failed; the next sweep retries.")]
    private static partial void LogSweepFailed(ILogger logger, Exception ex);
}

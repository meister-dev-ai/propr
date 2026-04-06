// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Repositories;

public sealed partial class EfProCursorTokenUsageRecorder
{
    [LoggerMessage(
        EventId = 4100,
        Level = LogLevel.Debug,
        Message = "Ignoring duplicate ProCursor token usage event for client {ClientId} request {RequestId}")]
    private static partial void LogDuplicateIgnored(ILogger logger, Guid clientId, string requestId);

    [LoggerMessage(
        EventId = 4101,
        Level = LogLevel.Debug,
        Message = "Ignoring duplicate ProCursor token usage event detected during save for client {ClientId} request {RequestId}")]
    private static partial void LogDuplicateCommittedIgnored(ILogger logger, Guid clientId, string requestId);

    [LoggerMessage(
        EventId = 4102,
        Level = LogLevel.Warning,
        Message = "Failed to record ProCursor token usage event for client {ClientId} request {RequestId}")]
    private static partial void LogRecordFailed(ILogger logger, Exception exception, Guid clientId, string requestId);
}

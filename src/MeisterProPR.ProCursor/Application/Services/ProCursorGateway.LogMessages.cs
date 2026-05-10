// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.Logging;

namespace MeisterProPR.ProCursor.Core;

public sealed partial class ProCursorGateway
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "ProCursor knowledge query unavailable for client {ClientId}; question '{Question}'")]
    private static partial void LogKnowledgeUnavailable(ILogger logger, Guid clientId, string question);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "ProCursor symbol query unavailable for client {ClientId}; symbol '{Symbol}'")]
    private static partial void LogSymbolUnavailable(ILogger logger, Guid clientId, string symbol);
}

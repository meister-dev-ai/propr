// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.Logging;

namespace MeisterProPR.Application.Services;

public sealed partial class ProCursorQueryService
{
    [LoggerMessage(Level = LogLevel.Information, Message = "ProCursor knowledge query unavailable for client {ClientId}: no eligible sources.")]
    private static partial void LogKnowledgeUnavailableNoEligibleSources(ILogger logger, Guid clientId);

    [LoggerMessage(Level = LogLevel.Information, Message = "ProCursor knowledge query unavailable for client {ClientId}: no ready snapshots.")]
    private static partial void LogKnowledgeUnavailableNoReadySnapshots(ILogger logger, Guid clientId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falling back to lexical ProCursor retrieval for client {ClientId} because query embedding generation is unavailable.")]
    private static partial void LogLexicalFallback(ILogger logger, Guid clientId, Exception ex);
}

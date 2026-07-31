// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.CodeInsights.Misses;

public sealed partial class CodeInsightMissHarvester
{
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Thread {ProviderThreadId} (client {ClientId}) carries no human comment; not a miss.")]
    private static partial void LogNothingHumanSaid(ILogger logger, string providerThreadId, Guid clientId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Thread {ProviderThreadId} (client {ClientId}) is one ProPR posted a finding as; not a miss.")]
    private static partial void LogOwnThread(ILogger logger, string providerThreadId, Guid clientId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Human thread {ProviderThreadId} (client {ClientId}) restates a finding ProPR raised; not a miss.")]
    private static partial void LogDuplicateOfFinding(ILogger logger, string providerThreadId, Guid clientId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Human thread {ProviderThreadId} (client {ClientId}) could not be judged; nothing harvested.")]
    private static partial void LogUnjudged(ILogger logger, string providerThreadId, Guid clientId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Human thread {ProviderThreadId} (client {ClientId}) harvested as a miss.")]
    private static partial void LogMissHarvested(ILogger logger, string providerThreadId, Guid clientId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Harvesting human thread {ProviderThreadId} (client {ClientId}) failed; "
                  + "the crawl continues unaffected.")]
    private static partial void LogHandlingFailed(
        ILogger logger,
        string providerThreadId,
        Guid clientId,
        Exception ex);
}

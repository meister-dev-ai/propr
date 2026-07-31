// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.CodeInsights.History;

public sealed partial class CodeInsightHistoryImporter
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Code Insights import for client {ClientId} did nothing: collection is not enabled.")]
    private static partial void LogGated(ILogger logger, Guid clientId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Code Insights import for client {ClientId}: {Jobs} jobs, {Findings} findings, "
                  + "{OutcomeThreads} resolved threads and {HumanThreads} human threads replayed.")]
    private static partial void LogImported(
        ILogger logger,
        Guid clientId,
        int jobs,
        int findings,
        int outcomeThreads,
        int humanThreads);
}

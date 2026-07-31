// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.CodeInsights.Persistence;

public sealed partial class CodeInsightFindingIngestionService
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Collecting the findings job {JobId} produced (client {ClientId}) failed; "
                  + "the review is unaffected.")]
    private static partial void LogHandlingFailed(ILogger logger, Guid jobId, Guid clientId, Exception ex);
}

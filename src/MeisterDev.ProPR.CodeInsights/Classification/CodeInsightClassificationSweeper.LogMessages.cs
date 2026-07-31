// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.CodeInsights.Classification;

public sealed partial class CodeInsightClassificationSweeper
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Code-insight classification sweep: considered {Considered}, classified {Classified}, "
                  + "failed {Failed}, skipped by gate {SkippedByGate}, backlog remaining {BacklogRemaining}")]
    private static partial void LogSweepCompleted(
        ILogger logger,
        int considered,
        int classified,
        int failed,
        int skippedByGate,
        int backlogRemaining);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Classifying finding {FindingId} failed; it will be retried.")]
    private static partial void LogFindingFailed(ILogger logger, Guid findingId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Could not record the failed classification attempt for finding {FindingId}; "
                  + "it may be retried more often than its ceiling allows.")]
    private static partial void LogAttemptRecordFailed(ILogger logger, Guid findingId, Exception ex);
}

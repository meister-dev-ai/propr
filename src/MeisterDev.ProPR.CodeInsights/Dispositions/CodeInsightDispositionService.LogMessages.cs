// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.CodeInsights.Dispositions;

public sealed partial class CodeInsightDispositionService
{
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Resolved thread {ThreadId} for client {ClientId} matches no collected finding; skipped.")]
    private static partial void LogNoMatchingFinding(ILogger logger, long threadId, Guid clientId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Finding {FindingId} recorded as {Disposition}.")]
    private static partial void LogDispositionRecorded(
        ILogger logger,
        Guid findingId,
        CodeInsightDisposition disposition);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "The wrong-versus-unwanted split for finding {FindingId} could not be judged; "
                  + "recorded as dismissed rather than as a false positive.")]
    private static partial void LogSplitUndecided(ILogger logger, Guid findingId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Recording a disposition for thread {ThreadId} (client {ClientId}) failed; "
                  + "the crawl continues unaffected.")]
    private static partial void LogHandlingFailed(ILogger logger, long threadId, Guid clientId, Exception ex);
}

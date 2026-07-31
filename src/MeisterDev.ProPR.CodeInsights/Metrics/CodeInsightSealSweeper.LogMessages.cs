// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.CodeInsights.Metrics;

public sealed partial class CodeInsightSealSweeper
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Sealed {SealedCount} pull request(s) whose closure the synchronization path never observed.")]
    private static partial void LogSweepSealed(ILogger logger, int sealedCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Selecting unsealed code-insight pull requests failed; the next sweep retries.")]
    private static partial void LogCandidateSelectionFailed(ILogger logger, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Asking the provider about PR {PullRequestId} (client {ClientId}) failed; it stays unmeasured.")]
    private static partial void LogExamineFailed(ILogger logger, long pullRequestId, Guid clientId, Exception ex);
}

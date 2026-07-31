// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.CodeInsights.Metrics;

public sealed partial class CodeInsightMetricSealer
{
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Sealed the code-insight measurement for PR {PullRequestId} (client {ClientId}): "
                  + "{ResolvedCount} resolved finding(s), {MissCount} miss(es).")]
    private static partial void LogSealed(
        ILogger logger,
        long pullRequestId,
        Guid clientId,
        int resolvedCount,
        int missCount);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "PR {PullRequestId} (client {ClientId}) closed with nothing to measure; no seal was written.")]
    private static partial void LogNothingToMeasure(ILogger logger, long pullRequestId, Guid clientId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Sealing the code-insight measurement for PR {PullRequestId} (client {ClientId}) failed; "
                  + "the crawl continues unaffected.")]
    private static partial void LogSealFailed(ILogger logger, long pullRequestId, Guid clientId, Exception ex);
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.AI;

public sealed partial class ThreadMemoryEmbedder
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to generate AI resolution summary; using fallback text")]
    private static partial void LogSummaryGenerationFailedCore(ILogger logger, Exception exception);

    private static void LogSummaryGenerationFailed(ILogger? logger, Exception exception)
    {
        if (logger is null)
        {
            return;
        }

        LogSummaryGenerationFailedCore(logger, exception);
    }
}

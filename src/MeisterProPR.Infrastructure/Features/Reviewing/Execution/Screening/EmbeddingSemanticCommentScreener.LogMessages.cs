// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Screening;

public sealed partial class EmbeddingSemanticCommentScreener
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Semantic comment screening failed; keeping the comment unscreened.")]
    private static partial void LogScreeningFailedCore(ILogger logger, Exception exception);

    private static void LogScreeningFailed(ILogger? logger, Exception exception)
    {
        if (logger is null)
        {
            return;
        }

        LogScreeningFailedCore(logger, exception);
    }
}

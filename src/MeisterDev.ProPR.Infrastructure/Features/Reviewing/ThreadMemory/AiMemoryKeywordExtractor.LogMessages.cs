// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.ThreadMemory;

internal sealed partial class AiMemoryKeywordExtractor
{
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "No insights-classification model is bound for client {ClientId}; memory keywords are skipped.")]
    private static partial void LogBindingUnavailable(ILogger logger, Guid clientId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Resolving the insights-classification runtime failed for client {ClientId} while extracting "
                  + "memory keywords. This is a fault, not a missing binding.")]
    private static partial void LogResolutionFailed(ILogger logger, Guid clientId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Extracting memory keywords failed for client {ClientId}; the memory is stored without them.")]
    private static partial void LogCallFailed(ILogger logger, Guid clientId, Exception ex);
}

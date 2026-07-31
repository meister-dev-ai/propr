// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.CodeInsights.Classification;

internal sealed partial class AiHumanMissClassifier
{
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "No insights-classification model is bound; human thread {ProviderThreadId} is not judged.")]
    private static partial void LogBindingUnavailable(ILogger logger, string providerThreadId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Resolving the insights-classification runtime failed for human thread {ProviderThreadId}. "
                  + "This is a fault, not a missing binding.")]
    private static partial void LogResolutionFailed(ILogger logger, string providerThreadId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The miss judgement call failed for human thread {ProviderThreadId}.")]
    private static partial void LogCallFailed(ILogger logger, string providerThreadId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The miss judgement for human thread {ProviderThreadId} was incomplete; it is not harvested.")]
    private static partial void LogUnusableResponse(ILogger logger, string providerThreadId);
}

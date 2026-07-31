// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.CodeInsights.Classification;

internal sealed partial class AiFindingTypeClassifier
{
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "No insights-classification model is bound for finding {FindingId}; it stays unclassified.")]
    private static partial void LogBindingUnavailable(ILogger logger, Guid findingId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Resolving the insights-classification runtime failed for finding {FindingId}. "
                  + "This is a fault, not a missing binding.")]
    private static partial void LogResolutionFailed(ILogger logger, Guid findingId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The insights-classification call failed for finding {FindingId}; it will be retried.")]
    private static partial void LogCallFailed(ILogger logger, Guid findingId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The insights classifier returned nothing usable for finding {FindingId} "
                  + "(no in-vocabulary type); it will be retried.")]
    private static partial void LogUnusableResponse(ILogger logger, Guid findingId);
}

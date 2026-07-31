// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.CodeInsights.Classification;

internal sealed partial class AiDisregardedFindingClassifier
{
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "No insights-classification model is bound for finding {FindingId}; "
                  + "the wrong-versus-unwanted split is left undecided.")]
    private static partial void LogBindingUnavailable(ILogger logger, Guid findingId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Resolving the insights-classification runtime failed for finding {FindingId}. "
                  + "This is a fault, not a missing binding.")]
    private static partial void LogResolutionFailed(ILogger logger, Guid findingId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The wrong-versus-unwanted judgement call failed for finding {FindingId}.")]
    private static partial void LogCallFailed(ILogger logger, Guid findingId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The wrong-versus-unwanted judgement for finding {FindingId} carried no verdict.")]
    private static partial void LogUnusableResponse(ILogger logger, Guid findingId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Rejection reason {Reason} contradicts the verdict wasWrong={WasWrong}; "
                  + "the reason is dropped and the verdict kept.")]
    private static partial void LogContradictoryReason(
        ILogger logger,
        CodeInsightRejectionReason reason,
        bool wasWrong);
}

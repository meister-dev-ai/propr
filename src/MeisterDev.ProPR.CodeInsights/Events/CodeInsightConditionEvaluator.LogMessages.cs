// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.CodeInsights.Events;

public sealed partial class CodeInsightConditionEvaluator
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Code-insight condition {EventType} is now {State} for client {ClientId} at {ObservedValue}.")]
    private static partial void LogTransitionRecorded(
        ILogger logger,
        CodeInsightEventType eventType,
        CodeInsightConditionState state,
        Guid clientId,
        double observedValue);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Evaluating code-insight quality conditions for client {ClientId} failed; "
                  + "the next cycle recomputes them.")]
    private static partial void LogEvaluationFailed(ILogger logger, Guid clientId, Exception ex);
}

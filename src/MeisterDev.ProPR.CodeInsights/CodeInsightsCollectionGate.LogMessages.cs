// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.CodeInsights;

public sealed partial class CodeInsightsCollectionGate
{
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Code Insights collection is off for client {ClientId}: no licensing service is registered")]
    private static partial void LogClosedWithoutLicensing(ILogger logger, Guid clientId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Code Insights collection gate could not be resolved for client {ClientId}; treating it as closed")]
    private static partial void LogGateResolutionFailed(ILogger logger, Guid clientId, Exception ex);
}

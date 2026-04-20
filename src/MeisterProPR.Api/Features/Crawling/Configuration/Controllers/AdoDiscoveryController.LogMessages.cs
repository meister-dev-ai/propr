// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Api.Controllers;

public sealed partial class AdoDiscoveryController
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "ADO discovery operation {Operation} could not find the requested resource.")]
    private static partial void LogDiscoveryResourceNotFound(ILogger logger, string operation, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "ADO discovery operation {Operation} was rejected.")]
    private static partial void LogDiscoveryRequestRejected(ILogger logger, string operation, Exception ex);
}

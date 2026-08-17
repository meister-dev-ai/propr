// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Api.Features.Clients.Controllers;

public sealed partial class ClientProviderDiscoveryController
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Provider discovery for {Provider} is not registered in this deployment")]
    private static partial void LogDiscoveryUnavailable(ILogger logger, ScmProvider provider, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "{Provider} refused a discovery request for client {ClientId}")]
    private static partial void LogDiscoveryRefused(
        ILogger logger,
        ScmProvider provider,
        Guid clientId,
        Exception ex);
}

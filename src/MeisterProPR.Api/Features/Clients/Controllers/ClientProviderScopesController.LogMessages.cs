// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Api.Features.Clients.Controllers;

public sealed partial class ClientProviderScopesController
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "Provider scope create conflict for client {ClientId}, connection {ConnectionId}, external scope {ExternalScopeId}.")]
    private static partial void LogProviderScopeConflict(
        ILogger logger,
        Guid clientId,
        Guid connectionId,
        string externalScopeId,
        Exception ex);
}

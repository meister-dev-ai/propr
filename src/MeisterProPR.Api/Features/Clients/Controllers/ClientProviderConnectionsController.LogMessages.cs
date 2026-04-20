// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Api.Features.Clients.Controllers;

public sealed partial class ClientProviderConnectionsController
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "Provider connection {Operation} conflict for client {ClientId}, provider {ProviderFamily}, host {HostBaseUrl}.")]
    private static partial void LogProviderConnectionConflict(
        ILogger logger,
        string operation,
        Guid clientId,
        ScmProvider providerFamily,
        string hostBaseUrl,
        Exception ex);
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Api.Features.Clients.Controllers;

public sealed partial class ClientReviewerIdentitiesController
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "Reviewer identity resolution failed for client {ClientId}, connection {ConnectionId}, search {Search}.")]
    private static partial void LogReviewerIdentityResolutionConflict(
        ILogger logger,
        Guid clientId,
        Guid connectionId,
        string search,
        Exception ex);
}

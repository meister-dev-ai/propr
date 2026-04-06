// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Api.Controllers;

public sealed partial class ClientsController
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Reviewer identity updated for client {ClientId} by {ActorType}")]
    private static partial void LogReviewerIdentityUpdated(ILogger logger, Guid clientId, string actorType);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "ADO organization scope {ScopeId} created for client {ClientId}; verification status {VerificationStatus}; enabled={IsEnabled}")]
    private static partial void LogAdoOrganizationScopeCreated(
        ILogger logger,
        Guid clientId,
        Guid scopeId,
        AdoOrganizationVerificationStatus verificationStatus,
        bool isEnabled);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "ADO organization scope {ScopeId} updated for client {ClientId}; verification status {VerificationStatus}; enabled={IsEnabled}")]
    private static partial void LogAdoOrganizationScopeUpdated(
        ILogger logger,
        Guid clientId,
        Guid scopeId,
        AdoOrganizationVerificationStatus verificationStatus,
        bool isEnabled);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "ADO organization scope {ScopeId} deleted for client {ClientId}")]
    private static partial void LogAdoOrganizationScopeDeleted(ILogger logger, Guid clientId, Guid scopeId);
}

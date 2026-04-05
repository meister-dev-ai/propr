// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.Logging;

namespace MeisterProPR.Application.Services;

public sealed partial class ProCursorGateway
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Guided ProCursor source creation failed because organization scope {ScopeId} was not found for client {ClientId}")]
    private static partial void LogGuidedOrganizationScopeMissing(ILogger logger, Guid clientId, Guid scopeId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Guided ProCursor source creation failed because organization scope {ScopeId} is disabled for client {ClientId}")]
    private static partial void LogGuidedOrganizationScopeDisabled(ILogger logger, Guid clientId, Guid scopeId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Guided ProCursor source '{CanonicalSourceValue}' is no longer available in project {ProjectId} for client {ClientId}")]
    private static partial void LogGuidedSourceUnavailable(ILogger logger, Guid clientId, string projectId, string canonicalSourceValue);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Guided ProCursor branch validation failed for source '{SourceDisplayName}' on client {ClientId}: {Reason}")]
    private static partial void LogGuidedBranchValidationFailed(ILogger logger, Guid clientId, string sourceDisplayName, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ProCursor knowledge query unavailable for client {ClientId}; question '{Question}'")]
    private static partial void LogKnowledgeUnavailable(ILogger logger, Guid clientId, string question);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ProCursor symbol query unavailable for client {ClientId}; symbol '{Symbol}'")]
    private static partial void LogSymbolUnavailable(ILogger logger, Guid clientId, string symbol);
}

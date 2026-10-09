// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Features.Clients.Models;

/// <summary>Non-secret authentication coordinates and replacement-secret availability.</summary>
public sealed record ScmAuthenticationConfiguration(
    ScmProvider ProviderFamily,
    string HostBaseUrl,
    ScmAuthenticationKind AuthenticationKind,
    string? UserName,
    string? OAuthTenantId,
    string? OAuthClientId,
    ScmApplicationId? AppId = null,
    ScmInstallationId? InstallationId = null,
    bool HasCompatibleSecretMaterial = true,
    bool ApplyHostBaseUrlEgressCheck = true);

/// <summary>Resolved patch metadata and persisted authentication field selection.</summary>
public sealed record EffectiveScmAuthentication(
    ScmAuthenticationKind AuthenticationKind,
    string? UserName,
    string? OAuthTenantId,
    string? OAuthClientId,
    bool HasCompatibleSecretMaterial,
    ScmApplicationId? AppId,
    ScmInstallationId? InstallationId,
    ScmApplicationId? PersistedAppId,
    ScmInstallationId? PersistedInstallationId,
    string? CandidateUserName,
    string? PersistedUserName);

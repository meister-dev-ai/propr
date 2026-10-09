// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Clients.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Domain.Enums;


namespace MeisterDev.ProPR.Application.Features.Clients.Services;

/// <summary>Coordinates native validation and existing verification capabilities.</summary>
public sealed class ProviderConnectionConfigurationService(IScmProviderRegistry registry, IClientScmScopeRepository scopes)
    : IProviderConnectionConfigurationService
{
    public IReadOnlyList<(string PropertyName, string Message)> Validate(ScmAuthenticationConfiguration configuration) =>
        registry.GetConnectionConfigurationPolicy(configuration.ProviderFamily).Validate(configuration);

    public static EffectiveScmAuthentication ResolvePatchAuthentication(
        IScmConnectionConfigurationPolicy policy,
        ScmAuthenticationKind? authenticationKind, string? userName,
        string? oAuthTenantId, string? oAuthClientId, ScmApplicationId? appId, ScmInstallationId? installationId, bool hasReplacementSecret,
        ClientScmConnectionDto existing)
    {
        var effectiveAuthenticationKind = authenticationKind ?? existing.AuthenticationKind;
        var effectiveUserName = userName ?? existing.UserName;
        var effectiveOAuthTenantId = oAuthTenantId ?? existing.OAuthTenantId;
        var effectiveOAuthClientId = oAuthClientId ?? existing.OAuthClientId;
        var hasCompatibleSecretMaterial = hasReplacementSecret
                                          || existing.AuthenticationKind == effectiveAuthenticationKind;
        var userNameMetadata = policy.PreparePatchUserName(effectiveAuthenticationKind, effectiveUserName, userName);
        var appMetadata = policy.PreparePatchAppMetadata(
            effectiveAuthenticationKind,
            appId, installationId, existing.AppId, existing.InstallationId);

        return new EffectiveScmAuthentication(
            effectiveAuthenticationKind,
            effectiveUserName,
            effectiveOAuthTenantId,
            effectiveOAuthClientId,
            hasCompatibleSecretMaterial,
            appMetadata.EffectiveAppId,
            appMetadata.EffectiveInstallationId,
            appMetadata.PersistedAppId,
            appMetadata.PersistedInstallationId,
            userNameMetadata.CandidateUserName,
            userNameMetadata.PersistedUserName);
    }

    public async Task VerifyAsync(ClientScmConnectionDto connection, CancellationToken ct = default)
    {
        var sourcePolicy = registry.GetReviewSourcePolicy(connection.ProviderFamily);
        if (sourcePolicy.RequiresOrganizationScope)
        {
            var enabledScope = (await scopes.GetByConnectionIdAsync(connection.ClientId, connection.Id, ct)).FirstOrDefault(scope => scope.IsEnabled);
            if (enabledScope is null)
            {
                throw new InvalidOperationException(sourcePolicy.MissingVerificationScopeMessage);
            }

            await registry.GetProviderAdminDiscoveryService(connection.ProviderFamily).ListProjectOptionsAsync(connection.ClientId, enabledScope.Id, ct);
        }
        else
        {
            await registry.GetRepositoryDiscoveryProvider(connection.ProviderFamily).ListScopesAsync(
                connection.ClientId, new ProviderHostRef(connection.ProviderFamily, connection.HostBaseUrl), ct);
        }

        registry.GetReviewerIdentityService(connection.ProviderFamily);
    }
}

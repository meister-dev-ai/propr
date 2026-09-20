// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Diagnostics;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>Operational provider connection details including decrypted secret material for provider adapters.</summary>
public sealed record ClientScmConnectionCredentialDto(
    Guid Id,
    Guid ClientId,
    ScmProvider ProviderFamily,
    string HostBaseUrl,
    ScmAuthenticationKind AuthenticationKind,
    string? OAuthTenantId,
    string? OAuthClientId,
    string DisplayName,
    string Secret,
    bool IsActive,
    long? GitHubAppId = null,
    long? GitHubAppInstallationId = null,
    string? UserName = null)
{
    /// <summary>Renders the connection without the secret; see <see cref="SecretSafeRendering" />.</summary>
    public override string ToString()
    {
        return $"{nameof(ClientScmConnectionCredentialDto)} {{ Id = {this.Id}, ClientId = {this.ClientId}, "
               + $"ProviderFamily = {this.ProviderFamily}, HostBaseUrl = {SecretSafeRendering.Address(this.HostBaseUrl)}, "
               + $"AuthenticationKind = {this.AuthenticationKind}, DisplayName = {this.DisplayName}, "
               + $"Secret = {SecretSafeRendering.Elide(this.Secret)}, IsActive = {this.IsActive} }}";
    }

    /// <summary>
    ///     Convenience constructor for non-OAuth credentials where tenant and client ID are not applicable.
    /// </summary>
    public ClientScmConnectionCredentialDto(
        Guid id,
        Guid clientId,
        ScmProvider providerFamily,
        string hostBaseUrl,
        ScmAuthenticationKind authenticationKind,
        string displayName,
        string secret,
        bool isActive)
        : this(
            id,
            clientId,
            providerFamily,
            hostBaseUrl,
            authenticationKind,
            null,
            null,
            displayName,
            secret,
            isActive)
    {
    }
}

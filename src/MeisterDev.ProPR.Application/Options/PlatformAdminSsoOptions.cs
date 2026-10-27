// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.Ai.Providers.Diagnostics;

namespace MeisterDev.ProPR.Application.Options;

/// <summary>
///     The OpenID Connect identity provider through which platform administrators sign in. The provider is
///     configured for the installation, not for a tenant, and it is in use when
///     <see cref="InstallationSsoProviderOptions.Authority" /> is set and the license grants single sign-on. An account
///     that signs in through it holds platform administrator rights as long as the ID token carries
///     <see cref="RequiredRole" /> in the claim <see cref="EffectiveRoleClaim" />.
/// </summary>
public sealed class PlatformAdminSsoOptions : InstallationSsoProviderOptions
{
    /// <summary>Configuration key of <see cref="InstallationSsoProviderOptions.Authority" />.</summary>
    public const string AuthorityKey = "MEISTER_PLATFORM_ADMIN_SSO_AUTHORITY";

    /// <summary>Configuration key of <see cref="InstallationSsoProviderOptions.MetadataUrl" />.</summary>
    public const string MetadataUrlKey = "MEISTER_PLATFORM_ADMIN_SSO_METADATA_URL";

    /// <summary>Configuration key of <see cref="InstallationSsoProviderOptions.ClientId" />.</summary>
    public const string ClientIdKey = "MEISTER_PLATFORM_ADMIN_SSO_CLIENT_ID";

    /// <summary>Configuration key of <see cref="InstallationSsoProviderOptions.ClientSecret" />.</summary>
    public const string ClientSecretKey = "MEISTER_PLATFORM_ADMIN_SSO_CLIENT_SECRET";

    /// <summary>Configuration key of <see cref="RoleClaim" />.</summary>
    public const string RoleClaimKey = "MEISTER_PLATFORM_ADMIN_SSO_ROLE_CLAIM";

    /// <summary>Configuration key of <see cref="RequiredRole" />.</summary>
    public const string RequiredRoleKey = "MEISTER_PLATFORM_ADMIN_SSO_REQUIRED_ROLE";

    /// <summary>Configuration key of <see cref="InstallationSsoProviderOptions.DisplayName" />.</summary>
    public const string DisplayNameKey = "MEISTER_PLATFORM_ADMIN_SSO_DISPLAY_NAME";

    /// <summary>The claim that carries the roles when <see cref="RoleClaim" /> is not set.</summary>
    public const string DefaultRoleClaim = "roles";

    /// <summary>The ID token claim that lists the roles, or <see langword="null" /> for <see cref="DefaultRoleClaim" />.</summary>
    public string? RoleClaim { get; set; }

    /// <summary>The role that the claim must contain for the account to hold platform administrator rights.</summary>
    public string? RequiredRole { get; set; }

    /// <summary>The ID token claim that lists the roles.</summary>
    public string EffectiveRoleClaim => string.IsNullOrWhiteSpace(this.RoleClaim) ? DefaultRoleClaim : this.RoleClaim;

    /// <summary>
    ///     Renders the settings without the client secret, and the two addresses without path, query and credentials;
    ///     see <see cref="SecretSafeRendering" />.
    /// </summary>
    public override string ToString()
    {
        return $"{nameof(PlatformAdminSsoOptions)} {{ Authority = {SecretSafeRendering.Address(this.Authority)}, "
               + $"MetadataUrl = {SecretSafeRendering.Address(this.MetadataUrl)}, "
               + $"ClientId = {this.ClientId}, ClientSecret = {SecretSafeRendering.Elide(this.ClientSecret)}, "
               + $"RoleClaim = {this.EffectiveRoleClaim}, RequiredRole = {this.RequiredRole}, "
               + $"DisplayName = {this.EffectiveDisplayName} }}";
    }
}

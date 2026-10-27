// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.Ai.Providers.Diagnostics;

namespace MeisterDev.ProPR.Application.Options;

/// <summary>
///     The OpenID Connect identity provider through which platform administrators sign in. The provider is
///     configured for the installation, not for a tenant, and it is in use when <see cref="Authority" /> is set and
///     the license grants single sign-on. An account that signs in through it holds platform administrator rights as
///     long as the ID token carries <see cref="RequiredRole" /> in the claim <see cref="EffectiveRoleClaim" />.
/// </summary>
public sealed class PlatformAdminSsoOptions
{
    /// <summary>Configuration key of <see cref="Authority" />.</summary>
    public const string AuthorityKey = "MEISTER_PLATFORM_ADMIN_SSO_AUTHORITY";

    /// <summary>Configuration key of <see cref="MetadataUrl" />.</summary>
    public const string MetadataUrlKey = "MEISTER_PLATFORM_ADMIN_SSO_METADATA_URL";

    /// <summary>Configuration key of <see cref="ClientId" />.</summary>
    public const string ClientIdKey = "MEISTER_PLATFORM_ADMIN_SSO_CLIENT_ID";

    /// <summary>Configuration key of <see cref="ClientSecret" />.</summary>
    public const string ClientSecretKey = "MEISTER_PLATFORM_ADMIN_SSO_CLIENT_SECRET";

    /// <summary>Configuration key of <see cref="RoleClaim" />.</summary>
    public const string RoleClaimKey = "MEISTER_PLATFORM_ADMIN_SSO_ROLE_CLAIM";

    /// <summary>Configuration key of <see cref="RequiredRole" />.</summary>
    public const string RequiredRoleKey = "MEISTER_PLATFORM_ADMIN_SSO_REQUIRED_ROLE";

    /// <summary>Configuration key of <see cref="DisplayName" />.</summary>
    public const string DisplayNameKey = "MEISTER_PLATFORM_ADMIN_SSO_DISPLAY_NAME";

    /// <summary>The claim that carries the roles when <see cref="RoleClaim" /> is not set.</summary>
    public const string DefaultRoleClaim = "roles";

    /// <summary>The longest display name the sign-in page shows.</summary>
    public const int MaxDisplayNameLength = 80;

    /// <summary>
    ///     The issuer URL of the provider, for example a Keycloak realm, or <see langword="null" /> when platform
    ///     administrators sign in with local accounts only.
    /// </summary>
    public string? Authority { get; set; }

    /// <summary>
    ///     The address of the discovery document, or <see langword="null" /> for the address below
    ///     <see cref="Authority" />. A separate address serves an installation that reaches the provider over
    ///     another network path than the browser does.
    /// </summary>
    public string? MetadataUrl { get; set; }

    /// <summary>The client ID of ProPR at the provider.</summary>
    public string? ClientId { get; set; }

    /// <summary>The client secret of ProPR at the provider.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>The ID token claim that lists the roles, or <see langword="null" /> for <see cref="DefaultRoleClaim" />.</summary>
    public string? RoleClaim { get; set; }

    /// <summary>The role that the claim must contain for the account to hold platform administrator rights.</summary>
    public string? RequiredRole { get; set; }

    /// <summary>The name of the provider on the sign-in page, or <see langword="null" /> for the host of the authority.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Whether the installation configured a provider for platform administrators.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(this.Authority);

    /// <summary>The ID token claim that lists the roles.</summary>
    public string EffectiveRoleClaim => string.IsNullOrWhiteSpace(this.RoleClaim) ? DefaultRoleClaim : this.RoleClaim;

    /// <summary>The address of the discovery document of the configured provider.</summary>
    public string MetadataAddress => string.IsNullOrWhiteSpace(this.MetadataUrl)
        ? $"{this.Authority?.TrimEnd('/')}/.well-known/openid-configuration"
        : this.MetadataUrl;

    /// <summary>The name of the provider on the sign-in page.</summary>
    public string EffectiveDisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(this.DisplayName))
            {
                return this.DisplayName;
            }

            return Uri.TryCreate(this.Authority, UriKind.Absolute, out var authority)
                ? authority.Host
                : "single sign-on";
        }
    }

    /// <summary>
    ///     Returns whether <paramref name="issuer" /> names the configured authority. A trailing slash is not part of
    ///     the comparison, because providers differ in whether they write one.
    /// </summary>
    /// <param name="issuer">The issuer of a discovery document or a token.</param>
    public bool IsAuthority(string? issuer)
    {
        return issuer is not null
               && this.Authority is not null
               && string.Equals(issuer.TrimEnd('/'), this.Authority.TrimEnd('/'), StringComparison.Ordinal);
    }

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

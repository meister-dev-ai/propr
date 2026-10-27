// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Application.Options;

/// <summary>
///     The settings of an OpenID Connect identity provider that the installation configures for one kind of sign-in. The
///     provider belongs to the installation, not to a tenant, and it is in use when <see cref="Authority" /> is set and
///     the license grants single sign-on.
/// </summary>
public abstract class InstallationSsoProviderOptions
{
    /// <summary>The longest display name the sign-in page shows.</summary>
    public const int MaxDisplayNameLength = 80;

    /// <summary>
    ///     The issuer URL of the provider, for example a Keycloak realm, or <see langword="null" /> when the installation
    ///     offers no sign-in through it.
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

    /// <summary>The name of the provider on the sign-in page, or <see langword="null" /> for the host of the authority.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Whether the installation configured the provider.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(this.Authority);

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
}

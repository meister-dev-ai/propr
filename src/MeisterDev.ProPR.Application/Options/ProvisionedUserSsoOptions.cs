// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.Ai.Providers.Diagnostics;

namespace MeisterDev.ProPR.Application.Options;

/// <summary>
///     The OpenID Connect identity provider through which provisioned users sign in. A hosting platform provisions these
///     users through the administration API, and ProPR knows each of them by the issuer of this provider and the subject
///     of the person at the provider. The provider is configured for the installation and is in use when
///     <see cref="InstallationSsoProviderOptions.Authority" /> is set and the license grants single sign-on.
/// </summary>
public sealed class ProvisionedUserSsoOptions : InstallationSsoProviderOptions
{
    /// <summary>Configuration key of <see cref="InstallationSsoProviderOptions.Authority" />.</summary>
    public const string AuthorityKey = "MEISTER_PROVISIONED_USER_SSO_AUTHORITY";

    /// <summary>Configuration key of <see cref="InstallationSsoProviderOptions.MetadataUrl" />.</summary>
    public const string MetadataUrlKey = "MEISTER_PROVISIONED_USER_SSO_METADATA_URL";

    /// <summary>Configuration key of <see cref="InstallationSsoProviderOptions.ClientId" />.</summary>
    public const string ClientIdKey = "MEISTER_PROVISIONED_USER_SSO_CLIENT_ID";

    /// <summary>Configuration key of <see cref="InstallationSsoProviderOptions.ClientSecret" />.</summary>
    public const string ClientSecretKey = "MEISTER_PROVISIONED_USER_SSO_CLIENT_SECRET";

    /// <summary>Configuration key of <see cref="InstallationSsoProviderOptions.DisplayName" />.</summary>
    public const string DisplayNameKey = "MEISTER_PROVISIONED_USER_SSO_DISPLAY_NAME";

    /// <summary>
    ///     The issuer under which ProPR records the identities of provisioned users: the authority without a trailing
    ///     slash, or <see langword="null" /> when the provider is not configured. Providers differ in whether their issuer
    ///     ends with a slash, so ProPR records it without one.
    /// </summary>
    public string? IdentityIssuer => this.IsConfigured ? this.Authority!.TrimEnd('/') : null;

    /// <summary>
    ///     Renders the settings without the client secret, and the two addresses without path, query and credentials;
    ///     see <see cref="SecretSafeRendering" />.
    /// </summary>
    public override string ToString()
    {
        return $"{nameof(ProvisionedUserSsoOptions)} {{ Authority = {SecretSafeRendering.Address(this.Authority)}, "
               + $"MetadataUrl = {SecretSafeRendering.Address(this.MetadataUrl)}, "
               + $"ClientId = {this.ClientId}, ClientSecret = {SecretSafeRendering.Elide(this.ClientSecret)}, "
               + $"DisplayName = {this.EffectiveDisplayName} }}";
    }
}

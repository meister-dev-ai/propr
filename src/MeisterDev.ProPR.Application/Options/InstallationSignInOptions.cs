// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Application.Options;

/// <summary>
///     The sign-ins that the installation offers beside its own identity providers. An installation that signs people in
///     only through the identity provider for platform administrators and the identity provider for provisioned users
///     turns the other sign-ins off.
/// </summary>
public sealed class InstallationSignInOptions
{
    /// <summary>Configuration key of <see cref="LocalSignInEnabled" />.</summary>
    public const string LocalSignInEnabledKey = "MEISTER_LOCAL_SIGN_IN_ENABLED";

    /// <summary>Configuration key of <see cref="TenantSsoEnabled" />.</summary>
    public const string TenantSsoEnabledKey = "MEISTER_TENANT_SSO_ENABLED";

    /// <summary>
    ///     Whether people sign in with a user name and a password: platform administrators at <c>/auth/login</c> and the
    ///     members of a tenant at the local sign-in of the tenant. While it is <see langword="false" />, ProPR refuses both
    ///     sign-ins for every account, whatever a tenant allows, and sets no password.
    /// </summary>
    public bool LocalSignInEnabled { get; set; } = true;

    /// <summary>
    ///     Whether the members of a tenant sign in through the identity providers that the tenant configures. While it is
    ///     <see langword="false" />, ProPR offers none of these providers, refuses their sign-ins and refuses to create or
    ///     enable one. The providers stay stored, and ProPR offers the enabled ones again once the setting is
    ///     <see langword="true" />.
    /// </summary>
    public bool TenantSsoEnabled { get; set; } = true;
}

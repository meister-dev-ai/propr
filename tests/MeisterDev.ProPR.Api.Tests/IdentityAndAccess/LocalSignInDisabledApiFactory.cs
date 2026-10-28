// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Options;
using Microsoft.AspNetCore.Hosting;

namespace MeisterDev.ProPR.Api.Tests.IdentityAndAccess;

/// <summary>
///     The host of <see cref="TenantAdministrationApiFactory" /> on an installation that turns off the password sign-in.
///     The installation configures the identity provider for platform administrators, because it does not start without
///     one.
/// </summary>
public sealed class LocalSignInDisabledApiFactory : TenantAdministrationApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.UseSetting(InstallationSignInOptions.LocalSignInEnabledKey, "false");
        builder.UseSetting(PlatformAdminSsoOptions.AuthorityKey, PlatformAdminSsoApiFactory.Authority);
        builder.UseSetting(PlatformAdminSsoOptions.ClientIdKey, PlatformAdminSsoApiFactory.ClientId);
        builder.UseSetting(PlatformAdminSsoOptions.ClientSecretKey, PlatformAdminSsoApiFactory.ClientSecret);
        builder.UseSetting(PlatformAdminSsoOptions.RequiredRoleKey, PlatformAdminSsoApiFactory.RequiredRole);
        builder.UseSetting(PlatformAdminSsoOptions.DisplayNameKey, PlatformAdminSsoApiFactory.DisplayName);
    }
}

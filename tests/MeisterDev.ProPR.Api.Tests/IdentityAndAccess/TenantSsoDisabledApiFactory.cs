// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Options;
using Microsoft.AspNetCore.Hosting;

namespace MeisterDev.ProPR.Api.Tests.IdentityAndAccess;

/// <summary>The host of <see cref="TenantAdministrationApiFactory" /> on an installation that turns off the identity providers of tenants.</summary>
public sealed class TenantSsoDisabledApiFactory : TenantAdministrationApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.UseSetting(InstallationSignInOptions.TenantSsoEnabledKey, "false");
    }
}

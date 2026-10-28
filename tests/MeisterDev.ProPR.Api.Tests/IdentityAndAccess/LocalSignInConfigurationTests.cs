// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Api.Tests.IdentityAndAccess;

/// <summary>
///     A host that turns off the password sign-in starts only when the installation configures the identity provider for
///     platform administrators, because no administrator could sign in otherwise.
/// </summary>
public sealed class LocalSignInConfigurationTests
{
    [Fact]
    public async Task WithoutThePasswordSignInAndWithoutTheProviderForPlatformAdministrators_TheHostDoesNotStart()
    {
        using var host = BuildHost(new Dictionary<string, string?> { [InstallationSignInOptions.LocalSignInEnabledKey] = "false" });

        var failure = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains(InstallationSignInOptions.LocalSignInEnabledKey, failure.Message, StringComparison.Ordinal);
        Assert.Contains(PlatformAdminSsoOptions.AuthorityKey, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutThePasswordSignInAndWithTheProviderForPlatformAdministrators_TheHostStarts()
    {
        using var host = BuildHost(
            new Dictionary<string, string?>
            {
                [InstallationSignInOptions.LocalSignInEnabledKey] = "false",
                [PlatformAdminSsoOptions.AuthorityKey] = PlatformAdminSsoApiFactory.Authority,
                [PlatformAdminSsoOptions.ClientIdKey] = PlatformAdminSsoApiFactory.ClientId,
                [PlatformAdminSsoOptions.ClientSecretKey] = PlatformAdminSsoApiFactory.ClientSecret,
                [PlatformAdminSsoOptions.RequiredRoleKey] = PlatformAdminSsoApiFactory.RequiredRole,
            });

        await host.StartAsync();
        await host.StopAsync();
    }

    /// <summary>A host with the identity module and the given settings.</summary>
    private static IHost BuildHost(Dictionary<string, string?> settings)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Production });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddIdentityAndAccessModule(builder.Configuration, builder.Environment);
        return builder.Build();
    }
}

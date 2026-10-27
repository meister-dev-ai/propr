// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Api.Tests.IdentityAndAccess;

/// <summary>
///     An installation without an identity provider for platform administrators offers none and answers its sign-in
///     endpoints with 404, and a host with a wrong setting does not start.
/// </summary>
public sealed class PlatformAdminSsoConfigurationTests(TenantAdministrationApiFactory factory)
    : IClassFixture<TenantAdministrationApiFactory>
{
    [Fact]
    public async Task WithoutAConfiguredProvider_TheProviderIsNotOfferedAndItsEndpointsAreNotFound()
    {
        factory.ResetLicensing();
        var client = factory.CreateClient();

        var options = JsonDocument.Parse(await client.GetStringAsync("/auth/options")).RootElement;
        var challenge = await client.GetAsync("/auth/platform-admin/challenge?returnUrl=http%3A%2F%2Flocalhost%3A5173%2Flogin%2Fcallback");
        var callback = await client.GetAsync("/auth/platform-admin/callback?code=code&state=state");

        Assert.Equal(JsonValueKind.Null, options.GetProperty("platformAdminSignIn").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, challenge.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, callback.StatusCode);
    }

    [Fact]
    public async Task WithAnAuthorityOverPlainHttp_TheHostDoesNotStart()
    {
        using var host = BuildHost(Environments.Production);

        var failure = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains(PlatformAdminSsoOptions.AuthorityKey, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithAnAuthorityOverPlainHttpInDevelopment_TheHostStarts()
    {
        using var host = BuildHost(Environments.Development);

        await host.StartAsync();
        await host.StopAsync();
    }

    /// <summary>A host with the identity module and a provider whose authority is reached over plain http.</summary>
    private static IHost BuildHost(string environmentName)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = environmentName });
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [PlatformAdminSsoOptions.AuthorityKey] = "http://id.example.test/realms/staff",
                [PlatformAdminSsoOptions.ClientIdKey] = "propr-platform",
                [PlatformAdminSsoOptions.ClientSecretKey] = "platform-client-secret",
                [PlatformAdminSsoOptions.RequiredRoleKey] = "operator",
            });
        builder.Services.AddIdentityAndAccessModule(builder.Configuration, builder.Environment);
        return builder.Build();
    }
}

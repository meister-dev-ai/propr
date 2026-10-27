// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Api.Tests.IdentityAndAccess;

/// <summary>
///     An installation without an identity provider for provisioned users offers no such sign-in and answers its sign-in
///     and provisioning endpoints with 404, and a host with a wrong setting of the provider does not start.
/// </summary>
public sealed class ProvisionedUserConfigurationTests(TenantAdministrationApiFactory factory)
    : IClassFixture<TenantAdministrationApiFactory>
{
    [Fact]
    public async Task WithoutAConfiguredProvider_TheSignInIsNotOfferedAndItsEndpointsAreNotFound()
    {
        factory.ResetLicensing();
        var client = factory.CreateClient();

        var options = JsonDocument.Parse(await client.GetStringAsync("/auth/options")).RootElement;
        var challenge = await client.GetAsync("/auth/provisioned-user/challenge?returnUrl=http%3A%2F%2Flocalhost%2Flogin%2Fcallback");
        var callback = await client.GetAsync("/auth/provisioned-user/callback?code=code&state=state");

        Assert.Equal(JsonValueKind.Null, options.GetProperty("provisionedUserSignIn").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, challenge.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, callback.StatusCode);
    }

    [Fact]
    public async Task WithoutAConfiguredProvider_TheProvisioningEndpointsAreNotFound()
    {
        factory.ResetLicensing();
        var tenantId = await factory.SeedTenantAsync($"customer-{Guid.NewGuid():N}", "Customer organization");
        var username = $"platform-{Guid.NewGuid():N}";
        var administratorId = await factory.SeedUserAsync(username, $"{username}@meister-dev.test", AppUserRole.Admin);
        using var platform = factory.CreateClient();
        platform.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(administratorId, AppUserRole.Admin));

        var responses = new[]
        {
            await platform.PutAsJsonAsync("/admin/provisioned-users/customer-1", new { email = "customer@customer.test" }),
            await platform.GetAsync($"/admin/tenants/{tenantId}/provisioned-members"),
            await platform.PutAsJsonAsync($"/admin/tenants/{tenantId}/provisioned-members/customer-1", new { clientIds = Array.Empty<Guid>() }),
            await platform.DeleteAsync($"/admin/tenants/{tenantId}/provisioned-members/customer-1"),
        };

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("provisioned_sign_in_not_configured", body.GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task WithAnAuthorityOverPlainHttp_TheHostDoesNotStart()
    {
        using var host = BuildHost(Environments.Production);

        var failure = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains(ProvisionedUserSsoOptions.AuthorityKey, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithAnAuthorityOverPlainHttpInDevelopment_TheHostStarts()
    {
        using var host = BuildHost(Environments.Development);

        await host.StartAsync();
        await host.StopAsync();
    }

    /// <summary>A host with the identity module and a provider for provisioned users reached over plain http.</summary>
    private static IHost BuildHost(string environmentName)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = environmentName });
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [ProvisionedUserSsoOptions.AuthorityKey] = "http://id.example.test/realms/customers",
                [ProvisionedUserSsoOptions.ClientIdKey] = "propr-cell",
                [ProvisionedUserSsoOptions.ClientSecretKey] = "cell-client-secret",
            });
        builder.Services.AddIdentityAndAccessModule(builder.Configuration, builder.Environment);
        return builder.Build();
    }
}

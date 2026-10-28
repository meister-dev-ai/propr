// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.ProPR.Api.Tests.IdentityAndAccess;

/// <summary>
///     An installation that turns off the identity providers of tenants offers none of them, refuses their sign-ins and
///     refuses to create or enable one. The providers stay stored and are offered again when the setting is on.
/// </summary>
public sealed class TenantSsoPolicyTests(TenantSsoDisabledApiFactory factory) : IClassFixture<TenantSsoDisabledApiFactory>
{
    [Fact]
    public async Task TenantLoginOptions_OfATenantWithAnEnabledProvider_OfferNoProvider()
    {
        factory.ResetLicensing();
        var tenantSlug = $"acme-{Guid.NewGuid():N}";
        var tenantId = await factory.SeedTenantAsync(tenantSlug, "Acme Corp");
        await factory.SeedSsoProviderAsync(tenantId, "Acme Entra");

        var response = await factory.CreateClient().GetAsync($"/auth/tenants/{tenantSlug}/providers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Empty(body.GetProperty("providers").EnumerateArray());
        Assert.True(body.GetProperty("localLoginEnabled").GetBoolean());
    }

    [Fact]
    public async Task AuthOptions_OfferThePasswordSignInAndNoSignInThroughTheProvidersOfTenants()
    {
        factory.ResetLicensing();

        var body = await ReadJsonAsync(await factory.CreateClient().GetAsync("/auth/options"));

        var methods = body.GetProperty("availableSignInMethods").EnumerateArray().Select(method => method.GetString()).ToList();
        Assert.Equal(["password"], methods);
    }

    [Fact]
    public async Task Challenge_OfAnEnabledProvider_IsRefused()
    {
        factory.ResetLicensing();
        factory.ResetExternalAuthResponses();
        var tenantSlug = $"acme-{Guid.NewGuid():N}";
        var tenantId = await factory.SeedTenantAsync(tenantSlug, "Acme Corp");
        var providerId = await factory.SeedSsoProviderAsync(tenantId, "Acme Entra");

        var response = await this.CreateBrowser().GetAsync($"/auth/external/challenge/{tenantSlug}/{providerId}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("tenant_sso_disabled", (await ReadJsonAsync(response)).GetProperty("error").GetString());
        Assert.Null(response.Headers.Location);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Callback_IsRefusedWithoutAskingTheProvider()
    {
        factory.ResetLicensing();
        factory.ResetExternalAuthResponses();
        var tenantSlug = $"acme-{Guid.NewGuid():N}";
        await factory.SeedTenantAsync(tenantSlug, "Acme Corp");

        var response = await this.CreateBrowser().GetAsync($"/auth/external/callback/{tenantSlug}?code=code&state=state");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("tenant_sso_disabled", (await ReadJsonAsync(response)).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("GET", "/auth/external/pending-link")]
    [InlineData("POST", "/auth/external/pending-link/confirm")]
    [InlineData("POST", "/auth/external/pending-link/new-account")]
    [InlineData("GET", "/auth/external/links")]
    public async Task WaitingSignInsAndLinks_AreRefused(string method, string path)
    {
        factory.ResetLicensing();

        var response = await this.CreateBrowser().SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("tenant_sso_disabled", (await ReadJsonAsync(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task ProviderCreation_IsRefusedAndStoresNothing()
    {
        factory.ResetLicensing();
        var (tenantId, token) = await this.SeedTenantAdministratorAsync();

        var response = await this.SendAsync(HttpMethod.Post, $"/admin/tenants/{tenantId}/sso-providers", token, ProviderRequest(isEnabled: false));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("tenant_sso_disabled", (await ReadJsonAsync(response)).GetProperty("error").GetString());
        Assert.Empty(await this.ListProvidersAsync(tenantId));
    }

    [Fact]
    public async Task ProviderUpdate_ThatEnablesAProvider_IsRefusedAndKeepsItDisabled()
    {
        factory.ResetLicensing();
        var (tenantId, token) = await this.SeedTenantAdministratorAsync();
        var providerId = await factory.SeedSsoProviderAsync(tenantId, "Acme Entra", isEnabled: false);

        var response = await this.SendAsync(HttpMethod.Put, $"/admin/tenants/{tenantId}/sso-providers/{providerId}", token, ProviderRequest(isEnabled: true));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("tenant_sso_disabled", (await ReadJsonAsync(response)).GetProperty("error").GetString());
        Assert.False(Assert.Single(await this.ListProvidersAsync(tenantId)).IsEnabled);
    }

    [Fact]
    public async Task ProviderUpdate_ThatKeepsAProviderEnabled_IsRefused()
    {
        factory.ResetLicensing();
        var (tenantId, token) = await this.SeedTenantAdministratorAsync();
        var providerId = await factory.SeedSsoProviderAsync(tenantId, "Acme Entra");

        var response = await this.SendAsync(HttpMethod.Put, $"/admin/tenants/{tenantId}/sso-providers/{providerId}", token, ProviderRequest(isEnabled: true));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Acme Entra", Assert.Single(await this.ListProvidersAsync(tenantId)).DisplayName);
    }

    [Fact]
    public async Task ProviderUpdate_ThatDisablesAProvider_AndItsDeletion_AreAccepted()
    {
        factory.ResetLicensing();
        var (tenantId, token) = await this.SeedTenantAdministratorAsync();
        var providerId = await factory.SeedSsoProviderAsync(tenantId, "Acme Entra");

        var update = await this.SendAsync(HttpMethod.Put, $"/admin/tenants/{tenantId}/sso-providers/{providerId}", token, ProviderRequest(isEnabled: false));
        var deletion = await this.SendAsync(HttpMethod.Delete, $"/admin/tenants/{tenantId}/sso-providers/{providerId}", token, null);

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deletion.StatusCode);
        Assert.Empty(await this.ListProvidersAsync(tenantId));
    }

    [Fact]
    public async Task StoredProvider_IsOfferedAgainWhenTheInstallationTurnsTheProvidersOn()
    {
        factory.ResetLicensing();
        var tenantSlug = $"acme-{Guid.NewGuid():N}";
        var tenantId = await factory.SeedTenantAsync(tenantSlug, "Acme Corp");
        await factory.SeedSsoProviderAsync(tenantId, "Acme Entra");
        using var turnedOn = factory.WithWebHostBuilder(builder => builder.UseSetting(InstallationSignInOptions.TenantSsoEnabledKey, "true"));

        var whileOff = await ReadJsonAsync(await factory.CreateClient().GetAsync($"/auth/tenants/{tenantSlug}/providers"));
        var whileOn = await ReadJsonAsync(await turnedOn.CreateClient().GetAsync($"/auth/tenants/{tenantSlug}/providers"));

        Assert.Empty(whileOff.GetProperty("providers").EnumerateArray());
        Assert.Equal("Acme Entra", Assert.Single(whileOn.GetProperty("providers").EnumerateArray()).GetProperty("displayName").GetString());
    }

    private static object ProviderRequest(bool isEnabled)
    {
        return new
        {
            displayName = "Acme Entra",
            providerKind = "EntraId",
            protocolKind = "Oidc",
            issuerOrAuthorityUrl = "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/v2.0",
            clientId = "acme-client-id",
            clientSecret = "super-secret",
            scopes = new[] { "openid", "profile", "email" },
            allowedEmailDomains = new[] { "acme.test" },
            isEnabled,
            autoCreateUsers = true,
        };
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private HttpClient CreateBrowser()
    {
        return factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
    }

    private async Task<(Guid TenantId, string Token)> SeedTenantAdministratorAsync()
    {
        var tenantId = await factory.SeedTenantAsync($"acme-{Guid.NewGuid():N}", "Acme Corp");
        var userId = await factory.SeedUserAsync($"tenant.admin.{Guid.NewGuid():N}", $"tenant.admin.{Guid.NewGuid():N}@acme.test");
        await factory.SeedTenantMembershipAsync(tenantId, userId, TenantRole.TenantAdministrator);
        return (tenantId, factory.GenerateToken(userId, AppUserRole.User));
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, object? body)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await factory.CreateClient().SendAsync(request);
    }

    private async Task<IReadOnlyList<MeisterDev.ProPR.Application.DTOs.TenantSsoProviderDto>> ListProvidersAsync(Guid tenantId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ITenantSsoProviderService>().ListAsync(tenantId);
    }
}

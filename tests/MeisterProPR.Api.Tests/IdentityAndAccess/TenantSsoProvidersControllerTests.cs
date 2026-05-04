// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Features.IdentityAndAccess;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Api.Tests.IdentityAndAccess;

public sealed class TenantSsoProvidersControllerTests(TenantAdministrationApiFactory factory)
    : IClassFixture<TenantAdministrationApiFactory>
{
    [Fact]
    public async Task GetProviders_WhenSsoCapabilityUnavailable_ReturnsPremiumFeatureUnavailable()
    {
        factory.ResetLicensing();
        factory.SetSsoCapabilityAvailability(false);

        var tenantId = await factory.SeedTenantAsync($"acme-{Guid.NewGuid():N}", "Acme Corp");
        var userId = await factory.SeedUserAsync($"tenant.admin.{Guid.NewGuid():N}", $"tenant.admin.{Guid.NewGuid():N}@acme.test");
        await factory.SeedTenantMembershipAsync(tenantId, userId, TenantRole.TenantAdministrator);

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/tenants/{tenantId}/sso-providers");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(userId, AppUserRole.User));

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("premium_feature_unavailable", body.GetProperty("error").GetString());
        Assert.Equal(PremiumCapabilityKey.SsoAuthentication, body.GetProperty("feature").GetString());
    }

    [Fact]
    public async Task PostProvider_TenantAdministratorForOwnTenant_Returns201WithoutSecretEcho()
    {
        factory.ResetLicensing();

        var tenantId = await factory.SeedTenantAsync("acme", "Acme Corp");
        var userId = await factory.SeedUserAsync("tenant.admin", "tenant.admin@acme.test");
        await factory.SeedTenantMembershipAsync(tenantId, userId, TenantRole.TenantAdministrator);

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/tenants/{tenantId}/sso-providers");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(userId, AppUserRole.User));
        request.Content = JsonContent.Create(
            new
            {
                displayName = "Acme Entra",
                providerKind = "EntraId",
                protocolKind = "Oidc",
                issuerOrAuthorityUrl = "https://login.microsoftonline.com/common/v2.0",
                clientId = "acme-client-id",
                clientSecret = "super-secret",
                scopes = new[] { "openid", "profile", "email" },
                allowedEmailDomains = new[] { "acme.test" },
                isEnabled = true,
                autoCreateUsers = true,
            });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Acme Entra", body.GetProperty("displayName").GetString());
        Assert.True(body.GetProperty("secretConfigured").GetBoolean());
        Assert.False(body.TryGetProperty("clientSecret", out _));
    }

    [Fact]
    public async Task GetProviders_TenantAdministrator_ReturnsOnlyOwnTenantProviders()
    {
        factory.ResetLicensing();

        var tenantId = await factory.SeedTenantAsync("acme", "Acme Corp");
        var otherTenantId = await factory.SeedTenantAsync("globex", "Globex Corp");
        var userId = await factory.SeedUserAsync("tenant.admin", "tenant.admin@acme.test");
        await factory.SeedTenantMembershipAsync(tenantId, userId, TenantRole.TenantAdministrator);
        await factory.SeedSsoProviderAsync(tenantId, "Acme Entra");
        await factory.SeedSsoProviderAsync(otherTenantId, "Globex Entra");

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/tenants/{tenantId}/sso-providers");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(userId, AppUserRole.User));

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Single(body.EnumerateArray());
        Assert.Equal("Acme Entra", body[0].GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task PutProvider_TenantAdministratorForOwnTenant_Returns200AndPersistsChanges()
    {
        factory.ResetLicensing();

        var tenantId = await factory.SeedTenantAsync("acme", "Acme Corp");
        var userId = await factory.SeedUserAsync("tenant.admin", "tenant.admin@acme.test");
        await factory.SeedTenantMembershipAsync(tenantId, userId, TenantRole.TenantAdministrator);
        var providerId = await factory.SeedSsoProviderAsync(tenantId, "Acme Entra");

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/tenants/{tenantId}/sso-providers/{providerId}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(userId, AppUserRole.User));
        request.Content = JsonContent.Create(
            new
            {
                displayName = "Acme Google",
                providerKind = "Google",
                protocolKind = "Oidc",
                issuerOrAuthorityUrl = "https://accounts.google.com",
                clientId = "google-client-id",
                clientSecret = "replacement-secret",
                scopes = new[] { "openid", "email" },
                allowedEmailDomains = new[] { "acme.test" },
                isEnabled = false,
                autoCreateUsers = false,
            });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Acme Google", body.GetProperty("displayName").GetString());
        Assert.False(body.GetProperty("isEnabled").GetBoolean());
    }

    [Fact]
    public async Task DeleteProvider_TenantAdministratorForOwnTenant_Returns204AndRemovesProvider()
    {
        factory.ResetLicensing();

        var tenantId = await factory.SeedTenantAsync("acme", "Acme Corp");
        var userId = await factory.SeedUserAsync("tenant.admin", "tenant.admin@acme.test");
        await factory.SeedTenantMembershipAsync(tenantId, userId, TenantRole.TenantAdministrator);
        var providerId = await factory.SeedSsoProviderAsync(tenantId, "Acme Entra");

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/tenants/{tenantId}/sso-providers/{providerId}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(userId, AppUserRole.User));

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        Assert.False(await dbContext.TenantSsoProviders.AnyAsync(provider => provider.Id == providerId));
    }

    [Fact]
    public async Task PostProvider_SystemTenant_Returns409Conflict()
    {
        factory.ResetLicensing();

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/tenants/{TenantCatalog.SystemTenantId}/sso-providers");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(Guid.NewGuid(), AppUserRole.Admin));
        request.Content = JsonContent.Create(
            new
            {
                displayName = "System Entra",
                providerKind = "EntraId",
                protocolKind = "Oidc",
                issuerOrAuthorityUrl = "https://login.microsoftonline.com/common/v2.0",
                clientId = "system-client-id",
                clientSecret = "super-secret",
                scopes = new[] { "openid", "profile", "email" },
                allowedEmailDomains = new[] { "meister.test" },
                isEnabled = true,
                autoCreateUsers = true,
            });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("The internal System tenant cannot be modified.", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task DeleteProvider_SystemTenant_Returns409Conflict()
    {
        factory.ResetLicensing();

        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            dbContext.TenantSsoProviders.Add(
                new TenantSsoProviderRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = TenantCatalog.SystemTenantId,
                    DisplayName = "System Entra",
                    ProviderKind = "EntraId",
                    ProtocolKind = "Oidc",
                    IssuerOrAuthorityUrl = "https://login.microsoftonline.com/common/v2.0",
                    ClientId = "system-client-id",
                    ClientSecretProtected = "protected::secret",
                    Scopes = ["openid", "profile", "email"],
                    AllowedEmailDomains = ["meister.test"],
                    IsEnabled = true,
                    AutoCreateUsers = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            await dbContext.SaveChangesAsync();
        }

        Guid providerId;
        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            providerId = await dbContext.TenantSsoProviders
                .Where(provider => provider.TenantId == TenantCatalog.SystemTenantId)
                .Select(provider => provider.Id)
                .SingleAsync();
        }

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/tenants/{TenantCatalog.SystemTenantId}/sso-providers/{providerId}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(Guid.NewGuid(), AppUserRole.Admin));

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("The internal System tenant cannot be modified.", body.GetProperty("error").GetString());
    }
}

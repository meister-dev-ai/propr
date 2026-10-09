// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Licensing.Dtos;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Auth;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess.Persistence;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.Support;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.IdentityAndAccess;

public sealed class TenantsControllerTests(TenantAdministrationApiFactory factory)
    : IClassFixture<TenantAdministrationApiFactory>
{
    [Fact]
    public async Task PostTenant_PlatformAdmin_Returns201AndPersistsTenant()
    {
        factory.ResetLicensing();

        var tenantSlug = $"acme-{Guid.NewGuid():N}";
        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/tenants");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(Guid.NewGuid(), AppUserRole.Admin));
        request.Content = JsonContent.Create(new { slug = tenantSlug, displayName = "Acme Corp" });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(tenantSlug, body.GetProperty("slug").GetString());
        Assert.Equal("Acme Corp", body.GetProperty("displayName").GetString());
        Assert.True(body.GetProperty("localLoginEnabled").GetBoolean());

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        Assert.True(await dbContext.Tenants.AnyAsync(tenant => tenant.Slug == tenantSlug));
    }

    [Fact]
    public async Task PatchTenant_TenantAdministratorForSameTenant_Returns200AndUpdatesPolicy()
    {
        factory.ResetLicensing();

        var tenantId = await factory.SeedTenantAsync("acme", "Acme Corp");
        var userId = await factory.SeedUserAsync("tenant.admin", "tenant.admin@acme.test");
        await factory.SeedTenantMembershipAsync(tenantId, userId, TenantRole.TenantAdministrator);

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/admin/tenants/{tenantId}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(userId, AppUserRole.User));
        request.Content = JsonContent.Create(new { displayName = "Acme Identity", localLoginEnabled = false });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Acme Identity", body.GetProperty("displayName").GetString());
        Assert.False(body.GetProperty("localLoginEnabled").GetBoolean());
    }

    // A permitted-family entry names a family by the identity key that family declares, and one no installed
    // family claims permits nothing. Refused on the write so a mistyped key is corrected on the form, rather than
    // stored and then quietly refusing every provider the tenant has.
    [Fact]
    public async Task PatchTenant_WithAPermittedFamilyNoInstalledProviderClaims_Returns400NamingIt()
    {
        factory.ResetLicensing();

        var tenantId = await factory.SeedTenantAsync("acme", "Acme Corp");
        var userId = await factory.SeedUserAsync("tenant.admin", "tenant.admin@acme.test");
        await factory.SeedTenantMembershipAsync(tenantId, userId, TenantRole.TenantAdministrator);

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/admin/tenants/{tenantId}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(userId, AppUserRole.User));
        request.Content = JsonContent.Create(new { allowedAiProviderKinds = new[] { "contoso/llm" } });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("contoso/llm", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // A family an installed add-in declares is stored, and the tenant reads it back under the key it was saved
    // with, which is the key its connections are stored against.
    [Fact]
    public async Task PatchTenant_WithAPermittedFamilyAnInstalledProviderClaims_Returns200AndKeepsIt()
    {
        factory.ResetLicensing();

        var tenantId = await factory.SeedTenantAsync("acme", "Acme Corp");
        var userId = await factory.SeedUserAsync("tenant.admin", "tenant.admin@acme.test");
        await factory.SeedTenantMembershipAsync(tenantId, userId, TenantRole.TenantAdministrator);

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/admin/tenants/{tenantId}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(userId, AppUserRole.User));
        request.Content = JsonContent.Create(new { allowedAiProviderKinds = new[] { "meisterdev/openAi" } });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(
            ["meisterdev/openAi"],
            body.GetProperty("allowedAiProviderKinds").EnumerateArray().Select(entry => entry.GetString()));
    }

    [Fact]
    public async Task PatchTenant_TenantAdministratorForOtherTenant_Returns403()
    {
        factory.ResetLicensing();

        var ownTenantId = await factory.SeedTenantAsync("acme", "Acme Corp");
        var otherTenantId = await factory.SeedTenantAsync("globex", "Globex Corp");
        var userId = await factory.SeedUserAsync("tenant.admin", "tenant.admin@acme.test");
        await factory.SeedTenantMembershipAsync(ownTenantId, userId, TenantRole.TenantAdministrator);

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/admin/tenants/{otherTenantId}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(userId, AppUserRole.User));
        request.Content = JsonContent.Create(new { displayName = "Globex Identity", localLoginEnabled = false });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ListTenants_PlatformAdmin_IncludesSystemTenantWithReadOnlyMetadata()
    {
        factory.ResetLicensing();

        var tenantId = await factory.SeedTenantAsync($"acme-{Guid.NewGuid():N}", "Acme Corp");

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/tenants");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(Guid.NewGuid(), AppUserRole.Admin));

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var tenants = body.EnumerateArray().ToList();
        Assert.Contains(
            tenants,
            tenant => tenant.GetProperty("id").GetGuid() == tenantId && tenant.GetProperty("isEditable").GetBoolean());

        var systemTenant = tenants.Single(tenant => tenant.GetProperty("id").GetGuid() == TenantCatalog.SystemTenantId);
        Assert.Equal(TenantCatalog.SystemTenantSlug, systemTenant.GetProperty("slug").GetString());
        Assert.Equal(TenantCatalog.SystemTenantDisplayName, systemTenant.GetProperty("displayName").GetString());
        Assert.False(systemTenant.GetProperty("localLoginEnabled").GetBoolean());
        Assert.False(systemTenant.GetProperty("isEditable").GetBoolean());
    }

    [Fact]
    public async Task PatchTenant_TenantAdministrator_SetsChangesAndClearsTheMonthlyCaps()
    {
        factory.ResetLicensing();

        var tenantId = await factory.SeedTenantAsync($"caps-{Guid.NewGuid():N}", "Caps Corp");
        var userId = await factory.SeedUserAsync($"caps.admin-{Guid.NewGuid():N}", "caps.admin@acme.test");
        await factory.SeedTenantMembershipAsync(tenantId, userId, TenantRole.TenantAdministrator);
        var httpClient = factory.CreateClient();

        var afterSet = await this.PatchBudgetAsync(httpClient, tenantId, userId, new { monthlySoftCapUsd = 800m, monthlyHardCapUsd = 1000m });
        Assert.Equal(HttpStatusCode.OK, afterSet.StatusCode);
        var setBody = JsonDocument.Parse(await afterSet.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(800m, setBody.GetProperty("budget").GetProperty("monthlySoftCapUsd").GetDecimal());
        Assert.Equal(1000m, setBody.GetProperty("budget").GetProperty("monthlyHardCapUsd").GetDecimal());

        var afterChange = await this.PatchBudgetAsync(httpClient, tenantId, userId, new { monthlySoftCapUsd = 400m, monthlyHardCapUsd = 500m });
        Assert.Equal(HttpStatusCode.OK, afterChange.StatusCode);
        var changeBody = JsonDocument.Parse(await afterChange.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(400m, changeBody.GetProperty("budget").GetProperty("monthlySoftCapUsd").GetDecimal());
        Assert.Equal(500m, changeBody.GetProperty("budget").GetProperty("monthlyHardCapUsd").GetDecimal());

        var afterClear = await this.PatchBudgetAsync(
            httpClient, tenantId, userId, new { monthlySoftCapUsd = (decimal?)null, monthlyHardCapUsd = (decimal?)null });
        Assert.Equal(HttpStatusCode.OK, afterClear.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var stored = await dbContext.Tenants.AsNoTracking().SingleAsync(tenant => tenant.Id == tenantId);
        Assert.Null(stored.MonthlyBudgetSoftCapUsd);
        Assert.Null(stored.MonthlyBudgetHardCapUsd);
    }

    [Fact]
    public async Task PatchTenant_TenantUserSettingTheMonthlyCaps_Returns403()
    {
        factory.ResetLicensing();

        var tenantId = await factory.SeedTenantAsync($"caps-{Guid.NewGuid():N}", "Caps Corp");
        var userId = await factory.SeedUserAsync($"caps.user-{Guid.NewGuid():N}", "caps.user@acme.test");
        await factory.SeedTenantMembershipAsync(tenantId, userId, TenantRole.TenantUser);

        var response = await this.PatchBudgetAsync(factory.CreateClient(), tenantId, userId, new { monthlyHardCapUsd = 100m });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PatchTenant_MonthlyCapsWithoutTheBudgetingCapability_ReportsThePremiumRefusal()
    {
        factory.ResetLicensing();
        factory.SetCapabilityAvailability(PremiumCapabilityKey.Budgeting, isAvailable: false, "Budgeting requires a commercial license.");

        try
        {
            var tenantId = await factory.SeedTenantAsync($"caps-{Guid.NewGuid():N}", "Caps Corp");
            var userId = await factory.SeedUserAsync($"caps.admin-{Guid.NewGuid():N}", "caps.admin@acme.test");
            await factory.SeedTenantMembershipAsync(tenantId, userId, TenantRole.TenantAdministrator);

            var response = await this.PatchBudgetAsync(factory.CreateClient(), tenantId, userId, new { monthlyHardCapUsd = 100m });

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("premium_feature_unavailable", body.GetProperty("error").GetString());
            Assert.Equal(PremiumCapabilityKey.Budgeting, body.GetProperty("feature").GetString());
        }
        finally
        {
            // The licensing service lives on the shared fixture, so an assertion that fails here would leave
            // Budgeting unavailable for every test that runs after this one.
            factory.ResetLicensing();
        }
    }

    [Fact]
    public async Task PatchTenant_SoftCapAboveTheHardCap_Returns400()
    {
        factory.ResetLicensing();

        var tenantId = await factory.SeedTenantAsync($"caps-{Guid.NewGuid():N}", "Caps Corp");
        var userId = await factory.SeedUserAsync($"caps.admin-{Guid.NewGuid():N}", "caps.admin@acme.test");
        await factory.SeedTenantMembershipAsync(tenantId, userId, TenantRole.TenantAdministrator);

        var response = await this.PatchBudgetAsync(factory.CreateClient(), tenantId, userId, new { monthlySoftCapUsd = 200m, monthlyHardCapUsd = 100m });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    // Beyond the magnitude the numeric(18,6) cap column holds, and finer than the scale it keeps.
    [InlineData("1000000000000")]
    [InlineData("0.0000001")]
    public async Task PatchTenant_CapOutsideTheStorableRange_Returns400(string hardCapUsd)
    {
        factory.ResetLicensing();

        var tenantId = await factory.SeedTenantAsync($"caps-{Guid.NewGuid():N}", "Caps Corp");
        var userId = await factory.SeedUserAsync($"caps.admin-{Guid.NewGuid():N}", "caps.admin@acme.test");
        await factory.SeedTenantMembershipAsync(tenantId, userId, TenantRole.TenantAdministrator);

        var response = await this.PatchBudgetAsync(
            factory.CreateClient(),
            tenantId,
            userId,
            new { monthlyHardCapUsd = decimal.Parse(hardCapUsd, CultureInfo.InvariantCulture) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var stored = await dbContext.Tenants.AsNoTracking().SingleAsync(tenant => tenant.Id == tenantId);
        Assert.Null(stored.MonthlyBudgetHardCapUsd);
    }

    [Fact]
    public async Task PatchTenant_TenantAdministrator_SetsAndClearsThePerFileLimits()
    {
        factory.ResetLicensing();

        var tenantId = await factory.SeedTenantAsync($"limits-{Guid.NewGuid():N}", "Limits Corp");
        var userId = await factory.SeedUserAsync($"limits.admin-{Guid.NewGuid():N}", "limits.admin@acme.test");
        await factory.SeedTenantMembershipAsync(tenantId, userId, TenantRole.TenantAdministrator);
        var httpClient = factory.CreateClient();

        var afterSet = await this.PatchTenantAsync(
            httpClient, tenantId, userId, new { reviewLimits = new { maxFileSizeBytes = 262_144, maxStructuralParseBytes = 131_072 } });
        Assert.Equal(HttpStatusCode.OK, afterSet.StatusCode);
        var body = JsonDocument.Parse(await afterSet.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(262_144, body.GetProperty("reviewLimits").GetProperty("maxFileSizeBytes").GetInt32());
        Assert.Equal(131_072, body.GetProperty("reviewLimits").GetProperty("maxStructuralParseBytes").GetInt32());

        var afterClear = await this.PatchTenantAsync(
            httpClient, tenantId, userId, new { reviewLimits = new { maxFileSizeBytes = (int?)null, maxStructuralParseBytes = (int?)null } });
        Assert.Equal(HttpStatusCode.OK, afterClear.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var stored = await dbContext.Tenants.AsNoTracking().SingleAsync(tenant => tenant.Id == tenantId);
        Assert.Null(stored.AiMaxFileSizeBytes);
        Assert.Null(stored.AiMaxStructuralParseBytes);
    }

    // The per-file limits bound what a tenant's reviews may spend, so they sit behind the same licensed
    // capability as the tenant's budget caps.
    [Fact]
    public async Task PatchTenant_PerFileLimitsWithoutTheBudgetingCapability_ReportsThePremiumRefusal()
    {
        factory.ResetLicensing();
        factory.SetCapabilityAvailability(PremiumCapabilityKey.Budgeting, isAvailable: false, "Budgeting requires a commercial license.");

        try
        {
            var tenantId = await factory.SeedTenantAsync($"limits-{Guid.NewGuid():N}", "Limits Corp");
            var userId = await factory.SeedUserAsync($"limits.admin-{Guid.NewGuid():N}", "limits.admin@acme.test");
            await factory.SeedTenantMembershipAsync(tenantId, userId, TenantRole.TenantAdministrator);

            var refused = await this.PatchTenantAsync(factory.CreateClient(), tenantId, userId, new { reviewLimits = new { maxFileSizeBytes = 262_144 } });

            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("premium_feature_unavailable", body.GetProperty("error").GetString());
            Assert.Equal(PremiumCapabilityKey.Budgeting, body.GetProperty("feature").GetString());

            // The message names what this request asked to set. A request that carried no spend cap must not
            // be refused with a message about spend caps.
            var message = body.GetProperty("message").GetString();
            Assert.Contains("per-file review limits", message, StringComparison.Ordinal);
            Assert.DoesNotContain("spend caps", message, StringComparison.Ordinal);

            // The same patch with the capability in force, so the refusal is the licence and nothing else
            // about the request.
            factory.ResetLicensing();
            var accepted = await this.PatchTenantAsync(factory.CreateClient(), tenantId, userId, new { reviewLimits = new { maxFileSizeBytes = 262_144 } });

            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            var acceptedBody = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(262_144, acceptedBody.GetProperty("reviewLimits").GetProperty("maxFileSizeBytes").GetInt32());
        }
        finally
        {
            // The licensing service lives on the shared fixture, so an assertion that fails here would leave
            // Budgeting unavailable for every test that runs after this one.
            factory.ResetLicensing();
        }
    }

    [Fact]
    public async Task PatchTenant_CapsAndLimitsWithoutTheBudgetingCapability_NamesBothInTheRefusal()
    {
        factory.ResetLicensing();
        factory.SetCapabilityAvailability(PremiumCapabilityKey.Budgeting, isAvailable: false, "Budgeting requires a commercial license.");

        try
        {
            var tenantId = await factory.SeedTenantAsync($"both-{Guid.NewGuid():N}", "Both Corp");
            var userId = await factory.SeedUserAsync($"both.admin-{Guid.NewGuid():N}", "both.admin@acme.test");
            await factory.SeedTenantMembershipAsync(tenantId, userId, TenantRole.TenantAdministrator);

            var refused = await this.PatchTenantAsync(
                factory.CreateClient(),
                tenantId,
                userId,
                new { budget = new { monthlyHardCapUsd = 100m }, reviewLimits = new { maxFileSizeBytes = 262_144 } });

            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement;
            var message = body.GetProperty("message").GetString();
            Assert.Contains("spend caps", message, StringComparison.Ordinal);
            Assert.Contains("per-file review limits", message, StringComparison.Ordinal);
        }
        finally
        {
            // The licensing service lives on the shared fixture, so an assertion that fails here would leave
            // Budgeting unavailable for every test that runs after this one.
            factory.ResetLicensing();
        }
    }

    [Fact]
    public async Task PatchTenant_PerFileLimitBelowTheAcceptedRange_Returns400()
    {
        factory.ResetLicensing();

        var tenantId = await factory.SeedTenantAsync($"limits-{Guid.NewGuid():N}", "Limits Corp");
        var userId = await factory.SeedUserAsync($"limits.admin-{Guid.NewGuid():N}", "limits.admin@acme.test");
        await factory.SeedTenantMembershipAsync(tenantId, userId, TenantRole.TenantAdministrator);

        var response = await this.PatchTenantAsync(factory.CreateClient(), tenantId, userId, new { reviewLimits = new { maxFileSizeBytes = 512 } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PatchTenant_TenantUserSettingThePerFileLimits_Returns403()
    {
        factory.ResetLicensing();

        var tenantId = await factory.SeedTenantAsync($"limits-{Guid.NewGuid():N}", "Limits Corp");
        var userId = await factory.SeedUserAsync($"limits.user-{Guid.NewGuid():N}", "limits.user@acme.test");
        await factory.SeedTenantMembershipAsync(tenantId, userId, TenantRole.TenantUser);

        var response = await this.PatchTenantAsync(factory.CreateClient(), tenantId, userId, new { reviewLimits = new { maxFileSizeBytes = 262_144 } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<HttpResponseMessage> PatchTenantAsync(HttpClient httpClient, Guid tenantId, Guid userId, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/admin/tenants/{tenantId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateToken(userId, AppUserRole.User));
        request.Content = JsonContent.Create(payload);
        return await httpClient.SendAsync(request);
    }

    private async Task<HttpResponseMessage> PatchBudgetAsync(HttpClient httpClient, Guid tenantId, Guid userId, object budget)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/admin/tenants/{tenantId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateToken(userId, AppUserRole.User));
        request.Content = JsonContent.Create(new { budget });
        return await httpClient.SendAsync(request);
    }

    [Fact]
    public async Task PatchTenant_SystemTenant_Returns409Conflict()
    {
        factory.ResetLicensing();

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/admin/tenants/{TenantCatalog.SystemTenantId}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(Guid.NewGuid(), AppUserRole.Admin));
        request.Content = JsonContent.Create(new { displayName = "Renamed System", localLoginEnabled = true });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("The internal System tenant cannot be modified.", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ListTenants_WithoutMultiTenancy_ReturnsOnlySystemTenant()
    {
        factory.ResetLicensing();
        factory.SetEdition(InstallationEdition.Community);
        await factory.SeedTenantAsync($"acme-{Guid.NewGuid():N}", "Acme Corp");

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/tenants");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(Guid.NewGuid(), AppUserRole.Admin));

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var tenants = body.EnumerateArray().ToList();
        Assert.Single(tenants);
        Assert.Equal(TenantCatalog.SystemTenantId, tenants[0].GetProperty("id").GetGuid());
        Assert.False(tenants[0].GetProperty("isEditable").GetBoolean());
    }

    [Fact]
    public async Task PostTenant_WithoutMultiTenancy_Returns409Conflict()
    {
        factory.ResetLicensing();
        factory.SetEdition(InstallationEdition.Community);

        var tenantSlug = $"acme-{Guid.NewGuid():N}";
        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/tenants");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(Guid.NewGuid(), AppUserRole.Admin));
        request.Content = JsonContent.Create(new { slug = tenantSlug, displayName = "Acme Corp" });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(
            "A commercial license is required to use more than the built-in System tenant, including in self-hosted deployments.",
            body.GetProperty("error").GetString());
    }

    // A tenant nobody has touched leaves reasoning capture to the installation switch, and the switch's current
    // value is reported alongside so a console can name what that default does here. Both switch values are
    // exercised: a response that always reported the shipped default would satisfy only one of them.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetTenant_NeverStatedReasoningPolicy_ReadsInstallationDefaultAndReportsTheSwitch(bool installationSwitch)
    {
        factory.ResetLicensing();

        var tenantId = await factory.SeedTenantAsync($"acme-{Guid.NewGuid():N}", "Acme Corp");

        using var configured = factory.WithWebHostBuilder(builder => builder.UseSetting(
            "AI_CAPTURE_REASONING_IN_PROTOCOL",
            installationSwitch ? "true" : "false"));

        var httpClient = configured.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/admin/tenants/{tenantId}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(Guid.NewGuid(), AppUserRole.Admin));

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("installationDefault", body.GetProperty("reasoningCapturePolicy").GetString());
        Assert.Equal(installationSwitch, body.GetProperty("installationDefaultCapturesReasoning").GetBoolean());

        using var scope = configured.Services.CreateScope();
        Assert.Equal(
            installationSwitch,
            scope.ServiceProvider.GetRequiredService<IOptions<AiReviewOptions>>().Value.CaptureReasoningInProtocol);
    }

    [Theory]
    [InlineData("enabled")]
    [InlineData("disabled")]
    [InlineData("installationDefault")]
    public async Task PatchTenant_ReasoningPolicyAlone_IsAcceptedAndStored(string requestedPolicy)
    {
        factory.ResetLicensing();

        var tenantId = await factory.SeedTenantAsync($"acme-{Guid.NewGuid():N}", "Acme Corp");
        var userId = await factory.SeedUserAsync(
            $"tenant.admin.{Guid.NewGuid():N}",
            $"tenant.admin.{Guid.NewGuid():N}@acme.test");
        await factory.SeedTenantMembershipAsync(tenantId, userId, TenantRole.TenantAdministrator);

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/admin/tenants/{tenantId}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(userId, AppUserRole.User));
        request.Content = JsonContent.Create(new { reasoningCapturePolicy = requestedPolicy });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(requestedPolicy, body.GetProperty("reasoningCapturePolicy").GetString());

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var stored = await dbContext.Tenants.AsNoTracking().SingleAsync(tenant => tenant.Id == tenantId);
        Assert.Equal(requestedPolicy, JsonNamingPolicy.CamelCase.ConvertName(stored.ReasoningCapturePolicy.ToString()));
    }

    [Fact]
    public async Task PatchTenant_ReasoningPolicy_TenantUserOfTheSameTenant_Returns403()
    {
        factory.ResetLicensing();

        var tenantId = await factory.SeedTenantAsync($"acme-{Guid.NewGuid():N}", "Acme Corp");
        var userId = await factory.SeedUserAsync(
            $"tenant.user.{Guid.NewGuid():N}",
            $"tenant.user.{Guid.NewGuid():N}@acme.test");
        await factory.SeedTenantMembershipAsync(tenantId, userId, TenantRole.TenantUser);

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/admin/tenants/{tenantId}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(userId, AppUserRole.User));
        request.Content = JsonContent.Create(new { reasoningCapturePolicy = "disabled" });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PatchTenant_ReasoningPolicy_AdministratorOfAnotherTenant_Returns403()
    {
        factory.ResetLicensing();

        var ownTenantId = await factory.SeedTenantAsync($"acme-{Guid.NewGuid():N}", "Acme Corp");
        var otherTenantId = await factory.SeedTenantAsync($"globex-{Guid.NewGuid():N}", "Globex Corp");
        var userId = await factory.SeedUserAsync(
            $"tenant.admin.{Guid.NewGuid():N}",
            $"tenant.admin.{Guid.NewGuid():N}@acme.test");
        await factory.SeedTenantMembershipAsync(ownTenantId, userId, TenantRole.TenantAdministrator);

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/admin/tenants/{otherTenantId}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(userId, AppUserRole.User));
        request.Content = JsonContent.Create(new { reasoningCapturePolicy = "disabled" });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PatchTenant_ReasoningPolicyOnTheSystemTenant_Returns409Conflict()
    {
        factory.ResetLicensing();

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/admin/tenants/{TenantCatalog.SystemTenantId}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(Guid.NewGuid(), AppUserRole.Admin));
        request.Content = JsonContent.Create(new { reasoningCapturePolicy = "disabled" });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}

public sealed class TenantAdministrationApiFactory : WebApplicationFactory<Program>
{
    private const string TestJwtSecret = "test-tenant-admin-jwt-secret-32chars!";

    private static readonly RsaSecurityKey OidcSigningKey = new(RSA.Create(2048)) { KeyId = "test-oidc-signing-key" };

    private readonly string _dbName = $"TestDb_TenantAdmin_{Guid.NewGuid()}";
    private readonly InMemoryDatabaseRoot _dbRoot = new();
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _externalAuthResponses = new();
    private readonly Lock _externalAuthResponsesLock = new();
    private readonly TestLicensingCapabilityService _licensingCapabilityService = new();
    private string? _publicBaseUrl;
    private MeisterDev.ProPR.Api.Features.IdentityAndAccess.Authentication.TenantMachineAuthenticationThrottle? _machineThrottle;

    public void SetMachineThrottle(int globalPermits, int perCredentialPermits, TimeSpan window)
    {
        this._machineThrottle =
            new MeisterDev.ProPR.Api.Features.IdentityAndAccess.Authentication.TenantMachineAuthenticationThrottle(globalPermits, perCredentialPermits, window);
    }

    /// <summary>
    ///     Credentials the test id_token factory signs with; the SSO validator is wired to trust the matching public key.
    /// </summary>
    public static SigningCredentials OidcSigningCredentials { get; } = new(OidcSigningKey, SecurityAlgorithms.RsaSha256);

    public void SetPublicBaseUrl(string? publicBaseUrl)
    {
        this._publicBaseUrl = publicBaseUrl;
    }

    public void SetSsoCapabilityAvailability(bool isAvailable, string? message = null)
    {
        this._licensingCapabilityService.SetCapabilityAvailability(
            PremiumCapabilityKey.SsoAuthentication,
            isAvailable,
            message);
    }

    public void SetCapabilityAvailability(string capabilityKey, bool isAvailable, string? message = null)
    {
        this._licensingCapabilityService.SetCapabilityAvailability(capabilityKey, isAvailable, message);
    }

    public void ResetLicensing()
    {
        this._licensingCapabilityService.Reset();
    }

    public void SetEdition(InstallationEdition edition)
    {
        this._licensingCapabilityService.SetEdition(edition);
    }

    public void ResetExternalAuthResponses()
    {
        lock (this._externalAuthResponsesLock)
        {
            this._externalAuthResponses.Clear();
        }
    }

    public void QueueExternalAuthResponse(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        ArgumentNullException.ThrowIfNull(responder);

        lock (this._externalAuthResponsesLock)
        {
            this._externalAuthResponses.Enqueue(responder);
        }
    }

    public string GenerateToken(Guid userId, AppUserRole globalRole)
    {
        return this.GenerateToken(userId.ToString(), globalRole);
    }

    public string GenerateToken(string subject, AppUserRole globalRole)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSecret));
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", subject),
                new Claim("global_role", globalRole.ToString()),
                new Claim(JwtRegisteredClaimNames.UniqueName, "tenant-admin"),
            ]),
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
            Issuer = "meisterpropr",
            Audience = "meisterpropr",
        };

        return handler.WriteToken(handler.CreateToken(descriptor));
    }

    public async Task<Guid> SeedTenantAsync(string slug, string displayName, bool localLoginEnabled = true)
    {
        using var scope = this.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var tenant = new TenantRecord
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            DisplayName = displayName,
            IsActive = true,
            LocalLoginEnabled = localLoginEnabled,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        dbContext.Tenants.Add(tenant);
        await dbContext.SaveChangesAsync();
        return tenant.Id;
    }

    public async Task<Guid> SeedUserAsync(string username, string email, AppUserRole globalRole = AppUserRole.User)
    {
        using var scope = this.Services.CreateScope();
        var userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Username = username,
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            PasswordHash = null,
            GlobalRole = globalRole,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await userRepository.AddAsync(user);
        return user.Id;
    }

    public async Task<string> GetUsernameAsync(Guid userId)
    {
        using var scope = this.Services.CreateScope();
        var userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var user = await userRepository.GetByIdAsync(userId);
        return user?.Username ?? string.Empty;
    }

    public async Task SetLocalPasswordAsync(Guid userId, string password)
    {
        using var scope = this.Services.CreateScope();
        var userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var passwordHashService = scope.ServiceProvider.GetRequiredService<IPasswordHashService>();
        await userRepository.UpdatePasswordHashAsync(userId, passwordHashService.Hash(password));
    }

    public async Task<Guid> SeedTenantMembershipAsync(Guid tenantId, Guid userId, TenantRole role)
    {
        using var scope = this.Services.CreateScope();
        var membershipService = scope.ServiceProvider.GetRequiredService<ITenantMembershipService>();
        var membership = await membershipService.UpsertAsync(tenantId, userId, role);
        return membership.Id;
    }

    public async Task SeedClientAssignmentAsync(Guid userId, Guid clientId, ClientRole role)
    {
        using var scope = this.Services.CreateScope();
        var userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        await userRepository.AddClientAssignmentAsync(
            new UserClientRole
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ClientId = clientId,
                Role = role,
                AssignedAt = DateTimeOffset.UtcNow,
            });
    }

    public async Task<Guid> SeedClientAsync(Guid tenantId, string displayName)
    {
        using var scope = this.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var client = new ClientRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DisplayName = displayName,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        dbContext.Clients.Add(client);
        await dbContext.SaveChangesAsync();
        return client.Id;
    }

    public async Task<Guid> SeedSsoProviderAsync(
        Guid tenantId,
        string displayName,
        string providerKind = "EntraId",
        string protocolKind = "Oidc",
        string? issuerOrAuthorityUrl = null,
        string? clientId = null,
        string? clientSecret = "super-secret",
        IEnumerable<string>? scopes = null,
        IEnumerable<string>? allowedEmailDomains = null,
        bool isEnabled = true,
        bool autoCreateUsers = true)
    {
        using var scope = this.Services.CreateScope();
        var providerService = scope.ServiceProvider.GetRequiredService<ITenantSsoProviderService>();
        var provider = await providerService.CreateAsync(
            tenantId,
            displayName,
            providerKind,
            protocolKind,
            issuerOrAuthorityUrl ?? "https://login.example.test/oidc",
            clientId ?? $"client-{Guid.NewGuid():N}",
            clientSecret,
            scopes ?? ["openid", "profile", "email"],
            allowedEmailDomains ?? ["acme.test"],
            isEnabled,
            autoCreateUsers);

        return provider.Id;
    }

    public async Task SeedExternalIdentityAsync(
        Guid tenantId,
        Guid userId,
        Guid providerId,
        string issuer,
        string subject,
        string email,
        bool emailVerified = true)
    {
        using var scope = this.Services.CreateScope();
        var userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        await userRepository.AddExternalIdentityAsync(
            new ExternalIdentity
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UserId = userId,
                SsoProviderId = providerId,
                Issuer = issuer,
                Subject = subject,
                Email = email,
                EmailVerified = emailVerified,
                CreatedAt = DateTimeOffset.UtcNow,
                LastSignInAt = DateTimeOffset.UtcNow,
            });
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true");
        builder.UseSetting("AI_ENDPOINT", "https://fake.openai.azure.com/");
        builder.UseSetting("AI_DEPLOYMENT", "gpt-4o");
        builder.UseSetting("MEISTER_JWT_SECRET", TestJwtSecret);

        if (!string.IsNullOrWhiteSpace(this._publicBaseUrl))
        {
            builder.UseSetting("MEISTER_PUBLIC_BASE_URL", this._publicBaseUrl);
        }

        var dbName = this._dbName;
        var dbRoot = this._dbRoot;
        builder.ConfigureServices(services =>
        {
            var secretProtectionCodec = Substitute.For<ISecretProtectionCodec>();
            secretProtectionCodec.Protect(Arg.Any<string>(), Arg.Any<string>())
                .Returns(callInfo => $"protected::{callInfo.ArgAt<string>(0)}");
            secretProtectionCodec.Unprotect(Arg.Any<string>(), Arg.Any<string>())
                .Returns(callInfo => callInfo.ArgAt<string>(0).Replace("protected::", string.Empty, StringComparison.Ordinal));
            secretProtectionCodec.IsProtected(Arg.Any<string>())
                .Returns(callInfo => callInfo.ArgAt<string>(0).StartsWith("protected::", StringComparison.Ordinal));

            services.AddSingleton<IJwtTokenService, JwtTokenService>();
            services.AddSingleton<IPasswordHashService, PasswordHashService>();
            services.AddSingleton(secretProtectionCodec);
            services.AddDbContext<MeisterProPRDbContext>(options => options.UseInMemoryDatabase(dbName, dbRoot));
            services.AddScoped<MeisterDev.ProPR.Api.Features.IdentityAndAccess.Authentication.TenantMachineCredentialService>();
            if (this._machineThrottle is not null)
            {
                services.RemoveAll<MeisterDev.ProPR.Api.Features.IdentityAndAccess.Authentication.TenantMachineAuthenticationThrottle>();
                services.AddSingleton(this._machineThrottle);
            }

            services.RemoveAll<ILicensingCapabilityService>();
            services.AddSingleton<ILicensingCapabilityService>(this._licensingCapabilityService);

            services.AddScoped<IUserRepository, AppUserRepository>();
            services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
            services.AddScoped<IUserPatRepository, UserPatRepository>();
            services.AddScoped<IClientAdminService, ClientAdminService>();
            // The licensing module is not composed here, so client creation admits through a gate with no ceiling.
            services.AddScoped<IStockQuotaGate, UnlimitedStockQuotaGate>();
            services
                .AddScoped<MeisterDev.ProPR.Application.Interfaces.IClientTokenUsageRepository,
                    MeisterDev.ProPR.Infrastructure.Repositories.ClientTokenUsageRepository>();
            services.AddScoped<ITenantAdminService, TenantAdminService>();
            services.AddScoped<ITenantMembershipService, TenantMembershipService>();
            services.AddScoped<ITenantMemberClientAccessService, TenantMemberClientAccessService>();
            services.AddScoped<ITenantSsoProviderService, TenantSsoProviderService>();
            services.AddScoped<ISessionFactory, SessionFactory>();
            services.AddHttpClient("TenantSsoAuth")
                .ConfigurePrimaryHttpMessageHandler(() => new StubHttpMessageHandler(this.DequeueExternalAuthResponse));

            services.AddSingleton(Substitute.For<IPullRequestFetcher>());
            services.AddSingleton(Substitute.For<IAdoCommentPoster>());
            services.AddSingleton(Substitute.For<IAssignedReviewDiscoveryService>());
            services.AddSingleton(Substitute.For<IPrStatusFetcher>());
            services.AddSingleton(Substitute.For<IThreadMemoryService>());

            var crawlRepo = Substitute.For<ICrawlConfigurationRepository>();
            crawlRepo.GetAllActiveAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([]));
            services.AddSingleton(crawlRepo);
            services.AddSingleton(Substitute.For<IWebhookConfigurationRepository>());
            services.AddSingleton(MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute());
            services.AddSingleton(Substitute.For<MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports.IPullRequestSynchronizationService>());

            services.AddSingleton(Substitute.For<IJobRepository>());
            services.AddScoped<MeisterDev.ProPR.Application.Features.Reviewing.Intake.Ports.ICustomerDashboardReader,
                MeisterDev.ProPR.Infrastructure.Features.Reviewing.Intake.Persistence.EfCustomerDashboardReader>();
            var history = Substitute.For<MeisterDev.ProPR.Application.Features.Reviewing.Intake.Ports.ICustomerReviewHistoryReader>();
            history.GetAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<MeisterDev.ProPR.Domain.Enums.JobStatus?>(), Arg.Any<CancellationToken>())
                .Returns(new MeisterDev.ProPR.Application.Features.Reviewing.Intake.Ports.CustomerReviewHistory(0, 1, 25, []));
            services.AddSingleton(history);
            services.AddScoped<IAccountLockoutService, AccountLockoutService>();

            // Validate id_tokens against a static, in-memory OIDC configuration keyed off the derived
            // metadata address, so the SSO callback exercises the real signature/issuer/audience/lifetime
            // path without any network fetch. The trusted public key matches OidcSigningCredentials.
            services.RemoveAll<ITenantOidcTokenValidator>();
            services.AddSingleton<ITenantOidcTokenValidator>(_ => new TenantOidcTokenValidator(
                metadataAddress =>
                {
                    var configuration = new OpenIdConnectConfiguration
                    {
                        Issuer = metadataAddress.Replace("/.well-known/openid-configuration", string.Empty, StringComparison.Ordinal),
                    };
                    configuration.SigningKeys.Add(OidcSigningKey);
                    return new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                },
                Substitute.For<ILogger<TenantOidcTokenValidator>>()));

            services.AddScoped<ITenantAuthService, TenantAuthService>();
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        using var scope = host.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        dbContext.Database.EnsureCreated();
        EnsureSystemTenantSeeded(dbContext);

        return host;
    }

    private static void EnsureSystemTenantSeeded(MeisterProPRDbContext dbContext)
    {
        if (dbContext.Tenants.Any(tenant => tenant.Id == TenantCatalog.SystemTenantId))
        {
            return;
        }

        dbContext.Tenants.Add(
            new TenantRecord
            {
                Id = TenantCatalog.SystemTenantId,
                Slug = TenantCatalog.SystemTenantSlug,
                DisplayName = TenantCatalog.SystemTenantDisplayName,
                IsActive = TenantCatalog.SystemTenantIsActive,
                LocalLoginEnabled = TenantCatalog.SystemTenantLocalLoginEnabled,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        dbContext.SaveChanges();
    }

    private HttpResponseMessage DequeueExternalAuthResponse(HttpRequestMessage request)
    {
        lock (this._externalAuthResponsesLock)
        {
            if (this._externalAuthResponses.Count == 0)
            {
                throw new InvalidOperationException($"No tenant SSO auth response queued for {request.Method} {request.RequestUri}.");
            }

            return this._externalAuthResponses.Dequeue()(request);
        }
    }

    private sealed class TestLicensingCapabilityService : ILicensingCapabilityService
    {
        private readonly Dictionary<string, CapabilitySnapshot> _capabilities = new(StringComparer.OrdinalIgnoreCase);
        private readonly StaticPremiumCapabilityCatalog _catalog = new();
        private readonly object _sync = new();
        private InstallationEdition _edition = InstallationEdition.Commercial;

        // Seeded on construction, because the real catalog answers for every key from the first request. A test
        // that never touches licensing still has its capability checks resolved rather than raising.
        public TestLicensingCapabilityService()
        {
            this.Reset();
        }

        public Task<LicensingSummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default)
        {
            lock (this._sync)
            {
                return Task.FromResult(
                    new LicensingSummaryDto(
                        this._edition,
                        this._edition == InstallationEdition.Commercial ? DateTimeOffset.UtcNow : null,
                        this._capabilities.Values
                            .Select(capability => new PremiumCapabilityDto(
                                capability.Key,
                                capability.DisplayName,
                                capability.RequiresCommercial,
                                capability.OverrideState,
                                capability.IsAvailable,
                                capability.Message,
                                capability.Reason))
                            .ToList()
                            .AsReadOnly()));
            }
        }

        public async Task<AuthOptionsDto> GetAuthOptionsAsync(CancellationToken cancellationToken = default)
        {
            var summary = await this.GetSummaryAsync(cancellationToken);
            var signInMethods = new List<string> { "password" };
            if (summary.Capabilities.Any(capability =>
                    string.Equals(capability.Key, PremiumCapabilityKey.SsoAuthentication, StringComparison.OrdinalIgnoreCase)
                    && capability.IsAvailable))
            {
                signInMethods.Add("sso");
            }

            return new AuthOptionsDto(summary.Edition, signInMethods.AsReadOnly(), summary.Capabilities);
        }

        public Task<CapabilitySnapshot> GetCapabilityAsync(string capabilityKey, CancellationToken cancellationToken = default)
        {
            lock (this._sync)
            {
                if (this._capabilities.TryGetValue(capabilityKey, out var capability))
                {
                    return Task.FromResult(capability);
                }
            }

            throw new KeyNotFoundException($"Unknown premium capability '{capabilityKey}'.");
        }

        public async ValueTask<bool> IsEnabledAsync(string capabilityKey, CancellationToken cancellationToken = default)
        {
            return (await this.GetCapabilityAsync(capabilityKey, cancellationToken)).IsAvailable;
        }

        public Task<LicensingSummaryDto> UpdateAsync(
            IReadOnlyCollection<CapabilityOverrideMutation> capabilityOverrides,
            Guid? actorUserId,
            CancellationToken cancellationToken = default)
        {
            return this.GetSummaryAsync(cancellationToken);
        }

        public void Reset()
        {
            lock (this._sync)
            {
                this._edition = InstallationEdition.Commercial;
                this._capabilities.Clear();

                foreach (var definition in this._catalog.GetAll())
                {
                    this._capabilities[definition.Key] = Available(definition);
                }
            }
        }

        public void SetCapabilityAvailability(string capabilityKey, bool isAvailable, string? message = null)
        {
            var definition = this._catalog.Get(capabilityKey)
                             ?? throw new KeyNotFoundException($"Unknown premium capability '{capabilityKey}'.");

            lock (this._sync)
            {
                this._capabilities[capabilityKey] = isAvailable
                    ? Available(definition)
                    : Unavailable(
                        definition,
                        message ?? definition.CommercialRequiredMessage,
                        PremiumCapabilityUnavailableReason.NotInLicense);
            }
        }

        /// <summary>
        ///     Puts the installation on an edition. The community edition also takes every capability that needs a
        ///     license away, as the real resolution does when no license is in force.
        /// </summary>
        public void SetEdition(InstallationEdition edition)
        {
            lock (this._sync)
            {
                this._edition = edition;

                if (edition != InstallationEdition.Community)
                {
                    return;
                }

                foreach (var definition in this._catalog.GetAll().Where(entry => entry.RequiresCommercial))
                {
                    this._capabilities[definition.Key] = Unavailable(
                        definition,
                        definition.CommercialRequiredMessage,
                        PremiumCapabilityUnavailableReason.NoLicense);
                }
            }
        }

        private static CapabilitySnapshot Available(PremiumCapabilityDefinition definition)
        {
            return new CapabilitySnapshot(
                definition.Key,
                definition.DisplayName,
                definition.RequiresCommercial,
                PremiumCapabilityOverrideState.Default,
                true,
                null);
        }

        private static CapabilitySnapshot Unavailable(
            PremiumCapabilityDefinition definition,
            string message,
            PremiumCapabilityUnavailableReason reason)
        {
            return new CapabilitySnapshot(
                definition.Key,
                definition.DisplayName,
                definition.RequiresCommercial,
                PremiumCapabilityOverrideState.Default,
                false,
                message,
                reason);
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = handler(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }
}

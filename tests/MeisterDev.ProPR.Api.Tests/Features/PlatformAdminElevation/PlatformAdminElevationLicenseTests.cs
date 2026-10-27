// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;

namespace MeisterDev.ProPR.Api.Tests.Features.PlatformAdminElevation;

/// <summary>
///     Elevations and the audit history of a tenant are part of the multi-tenancy capability. Without it, ProPR refuses
///     their endpoints as it refuses every other unlicensed premium endpoint.
/// </summary>
public sealed class PlatformAdminElevationLicenseTests(PlatformAdminElevationLicenseTests.UnlicensedHost host)
    : IClassFixture<PlatformAdminElevationLicenseTests.UnlicensedHost>
{
    [Theory]
    [InlineData("GET", "/admin/elevations/active")]
    [InlineData("POST", "/admin/elevations")]
    [InlineData("POST", "/admin/elevations/{elevationId}/end")]
    [InlineData("GET", "/admin/elevations")]
    [InlineData("GET", "/admin/elevations/{elevationId}")]
    [InlineData("GET", "/admin/elevations/{elevationId}/audit-entries")]
    public async Task WithoutMultiTenancyAnElevationEndpointIsRefusedAsUnlicensed(string method, string route)
    {
        var factory = host.Factory;
        var administrator = await factory.SeedUserAsync($"operator-{Guid.NewGuid():N}", AppUserRole.Admin);
        var tenant = await factory.SeedTenantAsync("Acme");
        using var http = factory.ClientFor(administrator, AppUserRole.Admin);
        using var request = new HttpRequestMessage(new HttpMethod(method), route.Replace("{elevationId}", Guid.NewGuid().ToString(), StringComparison.Ordinal));
        if (route == "/admin/elevations" && method == "POST")
        {
            request.Content = JsonContent.Create(new { tenantId = tenant, reason = "TICKET-21", durationMinutes = 10 });
        }

        await AssertUnlicensedAsync($"{method} {route}", await http.SendAsync(request));
        Assert.Empty(await factory.AuditEntriesAsync(tenant));
    }

    [Fact]
    public async Task WithoutMultiTenancyTheAuditHistoryOfATenantIsRefusedAsUnlicensed()
    {
        var factory = host.Factory;
        var administrator = await factory.SeedUserAsync($"operator-{Guid.NewGuid():N}", AppUserRole.Admin);
        var tenant = await factory.SeedTenantAsync("Acme");
        var tenantAdministrator = await factory.SeedUserAsync($"tenant-admin-{Guid.NewGuid():N}", AppUserRole.User);
        await factory.SeedMembershipAsync(tenant, tenantAdministrator, TenantRole.TenantAdministrator);
        using var asAdministrator = factory.ClientFor(administrator, AppUserRole.Admin);
        using var asTenantAdministrator = factory.ClientFor(tenantAdministrator, AppUserRole.User);

        await AssertUnlicensedAsync(
            "The request of the platform administrator for the System tenant",
            await asAdministrator.GetAsync($"/admin/tenants/{TenantCatalog.SystemTenantId}/audit-entries"));
        await AssertUnlicensedAsync(
            "The request of the platform administrator for another tenant",
            await asAdministrator.GetAsync($"/admin/tenants/{tenant}/audit-entries"));
        await AssertUnlicensedAsync(
            "The request of the tenant administrator",
            await asTenantAdministrator.GetAsync($"/admin/tenants/{tenant}/audit-entries"));
    }

    private static async Task AssertUnlicensedAsync(string request, HttpResponseMessage response)
    {
        using (response)
        {
            Assert.True(
                response.StatusCode == HttpStatusCode.Conflict,
                $"{request} was answered with {(int)response.StatusCode}, not 409.");
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("premium_feature_unavailable", body.GetProperty("error").GetString());
            Assert.Equal(PremiumCapabilityKey.MultiTenancy, body.GetProperty("feature").GetString());
        }
    }

    /// <summary>An API host whose license includes every capability except multi-tenancy.</summary>
    public sealed class UnlicensedHost : IDisposable
    {
        public ElevationApiFactory Factory { get; } = new() { MultiTenancyAvailable = false };

        public void Dispose()
        {
            this.Factory.Dispose();
        }
    }
}

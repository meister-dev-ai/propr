// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MeisterDev.ProPR.Application.Features.PlatformAdminElevation;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.ProPR.Api.Tests.Features.PlatformAdminElevation;

/// <summary>
///     A platform administrator starts, lists and ends elevations, and ProPR records each start, end and expiry in the
///     audit history that the tenant administrators read.
/// </summary>
public sealed class PlatformAdminElevationApiTests(ElevationApiFactory factory) : IClassFixture<ElevationApiFactory>
{
    [Fact]
    public async Task StartingAnElevationNeedsAReasonADurationWithinTheMaximumAndAClientOfTheTenant()
    {
        var scenario = await this.ScenarioAsync();
        var otherTenant = await factory.SeedTenantAsync("Globex");
        var otherClient = await factory.SeedClientAsync(otherTenant, "Globex client");
        using var http = factory.ClientFor(scenario.Administrator, AppUserRole.Admin);

        var status = await ReadJsonAsync(await http.GetAsync("/admin/elevations/active"));
        Assert.Equal(PlatformAdminElevationOptions.DefaultMaxDurationMinutes, status.GetProperty("maxDurationMinutes").GetInt32());

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await http.PostAsJsonAsync("/admin/elevations", new { tenantId = scenario.Tenant, reason = " ", durationMinutes = 10 })).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await http.PostAsJsonAsync(
                "/admin/elevations",
                new { tenantId = scenario.Tenant, reason = "TICKET-6", durationMinutes = PlatformAdminElevationOptions.DefaultMaxDurationMinutes + 1 }))
            .StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await http.PostAsJsonAsync(
                "/admin/elevations",
                new { tenantId = scenario.Tenant, clientId = otherClient, reason = "TICKET-6", durationMinutes = 10 })).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await http.PostAsJsonAsync("/admin/elevations", new { tenantId = Guid.NewGuid(), reason = "TICKET-6", durationMinutes = 10 })).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await http.PostAsJsonAsync(
                "/admin/elevations",
                new { tenantId = TenantCatalog.SystemTenantId, reason = "TICKET-6", durationMinutes = 10 })).StatusCode);
        Assert.Empty((await ReadJsonAsync(await http.GetAsync("/admin/elevations/active"))).GetProperty("active").EnumerateArray());
    }

    // A null client covers every client of the tenant. An empty identifier must not be read as null, because it would
    // start an elevation for a broader scope than the request named.
    [Fact]
    public async Task AnEmptyClientIdIsRefusedAndStartsNoElevationForTheWholeTenant()
    {
        var scenario = await this.ScenarioAsync();
        using var http = factory.ClientFor(scenario.Administrator, AppUserRole.Admin);

        var response = await http.PostAsJsonAsync(
            "/admin/elevations",
            new { tenantId = scenario.Tenant, clientId = Guid.Empty, reason = "TICKET-14", durationMinutes = 10 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            (await ReadJsonAsync(response)).GetProperty("errors").EnumerateObject(),
            error => string.Equals(error.Name, "clientId", StringComparison.OrdinalIgnoreCase));
        Assert.Empty((await ReadJsonAsync(await http.GetAsync("/admin/elevations/active"))).GetProperty("active").EnumerateArray());
        Assert.DoesNotContain(
            await factory.AuditEntriesAsync(scenario.Tenant),
            entry => entry.EventType == PlatformAdminElevationAuditEvents.Started);
    }

    // The offset of a page is the product of the page number and the page size, which exceeds the range of an int for
    // a large page number. A page behind the last entry is empty.
    [Fact]
    public async Task APageBehindTheLastEntryIsEmptyAlsoForTheLargestPageNumber()
    {
        var scenario = await this.ScenarioAsync();
        using var http = factory.ClientFor(scenario.Administrator, AppUserRole.Admin);
        await StartAsync(http, scenario.Tenant, scenario.ClientA, "TICKET-16", 30);
        var auditTotal = (await ReadJsonAsync(await http.GetAsync($"/admin/tenants/{scenario.Tenant}/audit-entries")))
            .GetProperty("totalCount").GetInt32();
        Assert.True(auditTotal > 0);

        var elevations = await http.GetAsync($"/admin/elevations?tenantId={scenario.Tenant}&page={int.MaxValue}&pageSize=2");
        var auditEntries = await http.GetAsync($"/admin/tenants/{scenario.Tenant}/audit-entries?page={int.MaxValue}&pageSize=200");

        Assert.Equal(HttpStatusCode.OK, elevations.StatusCode);
        var elevationPage = await ReadJsonAsync(elevations);
        Assert.Empty(elevationPage.GetProperty("items").EnumerateArray());
        Assert.Equal(1, elevationPage.GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.OK, auditEntries.StatusCode);
        var auditPage = await ReadJsonAsync(auditEntries);
        Assert.Empty(auditPage.GetProperty("items").EnumerateArray());
        Assert.Equal(auditTotal, auditPage.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task AnElevationIsActiveUntilAPlatformAdministratorEndsIt()
    {
        var scenario = await this.ScenarioAsync();
        using var http = factory.ClientFor(scenario.Administrator, AppUserRole.Admin);
        var elevation = await StartAsync(http, scenario.Tenant, scenario.ClientA, "TICKET-4711", 30);
        var elevationId = elevation.GetProperty("id").GetGuid();
        Assert.Equal("active", elevation.GetProperty("state").GetString());
        Assert.Equal(scenario.ClientA, elevation.GetProperty("clientId").GetGuid());
        Assert.Equal("Client A", elevation.GetProperty("clientDisplayName").GetString());

        var active = (await ReadJsonAsync(await http.GetAsync("/admin/elevations/active"))).GetProperty("active");
        Assert.Equal(elevationId, Assert.Single(active.EnumerateArray()).GetProperty("id").GetGuid());

        var ended = await http.PostAsync($"/admin/elevations/{elevationId}/end", null);
        Assert.Equal(HttpStatusCode.OK, ended.StatusCode);
        Assert.Equal("ended", (await ReadJsonAsync(ended)).GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync($"/admin/elevations/{elevationId}/end", null)).StatusCode);
        Assert.Empty((await ReadJsonAsync(await http.GetAsync("/admin/elevations/active"))).GetProperty("active").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync($"/admin/elevations/{Guid.NewGuid()}/end", null)).StatusCode);
    }

    [Fact]
    public async Task AnExpiredElevationIsNoLongerActiveAndItsExpiryIsRecordedOnce()
    {
        var scenario = await this.ScenarioAsync();
        using var http = factory.ClientFor(scenario.Administrator, AppUserRole.Admin);
        var elevationId = (await StartAsync(http, scenario.Tenant, scenario.ClientA, "TICKET-2", 15)).GetProperty("id").GetGuid();

        await factory.ExpireAsync(elevationId);

        Assert.Equal("expired", (await ReadJsonAsync(await http.GetAsync($"/admin/elevations/{elevationId}"))).GetProperty("state").GetString());
        Assert.Empty((await ReadJsonAsync(await http.GetAsync("/admin/elevations/active"))).GetProperty("active").EnumerateArray());
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync($"/admin/elevations/{elevationId}/end", null)).StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var elevations = scope.ServiceProvider.GetRequiredService<IPlatformAdminElevationService>();
            Assert.True(await elevations.RecordExpiriesAsync() >= 1);
            Assert.Equal(0, await elevations.RecordExpiriesAsync());
        }

        var expiries = (await factory.AuditEntriesAsync(scenario.Tenant))
            .Where(entry => entry.EventType == PlatformAdminElevationAuditEvents.Expired)
            .ToList();
        Assert.Equal(elevationId, Assert.Single(expiries).ElevationId);
    }

    [Fact]
    public async Task TheTenantAdministratorReadsTheStartAndTheEndOfAnElevationInTheAuditHistory()
    {
        var scenario = await this.ScenarioAsync();
        using var operatorHttp = factory.ClientFor(scenario.Administrator, AppUserRole.Admin);
        var elevationId = (await StartAsync(operatorHttp, scenario.Tenant, null, "TICKET-3 customer asked about a finding", 60))
            .GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await operatorHttp.PostAsync($"/admin/elevations/{elevationId}/end", null)).StatusCode);

        using var tenantHttp = factory.ClientFor(scenario.TenantAdministrator, AppUserRole.User);
        var history = await tenantHttp.GetAsync($"/admin/tenants/{scenario.Tenant}/audit-entries");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        var entries = (await ReadJsonAsync(history)).GetProperty("items").EnumerateArray().ToList();
        var started = Assert.Single(entries, entry => entry.GetProperty("eventType").GetString() == PlatformAdminElevationAuditEvents.Started);
        var ended = Assert.Single(entries, entry => entry.GetProperty("eventType").GetString() == PlatformAdminElevationAuditEvents.Ended);
        Assert.Equal(elevationId, started.GetProperty("elevationId").GetGuid());
        Assert.Equal(elevationId, ended.GetProperty("elevationId").GetGuid());
        Assert.Equal(scenario.Administrator, started.GetProperty("actorUserId").GetGuid());
        Assert.Contains("TICKET-3", started.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Contains("every client of the tenant", started.GetProperty("summary").GetString(), StringComparison.Ordinal);

        var operatorView = await ReadJsonAsync(await operatorHttp.GetAsync($"/admin/elevations/{elevationId}/audit-entries"));
        Assert.Equal(2, operatorView.GetArrayLength());
        var listed = await ReadJsonAsync(await operatorHttp.GetAsync($"/admin/elevations?tenantId={scenario.Tenant}"));
        Assert.Equal(elevationId, Assert.Single(listed.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task OnlyPlatformAdministratorsElevateAndOnlyTheAdministratorsOfTheTenantReadItsAuditHistory()
    {
        var scenario = await this.ScenarioAsync();
        var member = await factory.SeedUserAsync($"member-{Guid.NewGuid():N}", AppUserRole.User);
        await factory.SeedMembershipAsync(scenario.Tenant, member, TenantRole.TenantUser);
        var otherTenant = await factory.SeedTenantAsync("Globex");
        var otherAdministrator = await factory.SeedUserAsync($"globex-admin-{Guid.NewGuid():N}", AppUserRole.User);
        await factory.SeedMembershipAsync(otherTenant, otherAdministrator, TenantRole.TenantAdministrator);
        using var tenantHttp = factory.ClientFor(scenario.TenantAdministrator, AppUserRole.User);
        using var memberHttp = factory.ClientFor(member, AppUserRole.User);
        using var otherHttp = factory.ClientFor(otherAdministrator, AppUserRole.User);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await tenantHttp.PostAsJsonAsync("/admin/elevations", new { tenantId = scenario.Tenant, reason = "x", durationMinutes = 10 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenantHttp.GetAsync("/admin/elevations")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await memberHttp.GetAsync($"/admin/tenants/{scenario.Tenant}/audit-entries")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await otherHttp.GetAsync($"/admin/tenants/{scenario.Tenant}/audit-entries")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tenantHttp.GetAsync($"/admin/tenants/{scenario.Tenant}/audit-entries")).StatusCode);
    }

    private static async Task<JsonElement> StartAsync(HttpClient http, Guid tenantId, Guid? clientId, string reason, int minutes)
    {
        var response = await http.PostAsJsonAsync(
            "/admin/elevations",
            new { tenantId, clientId, reason, durationMinutes = minutes });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<Scenario> ScenarioAsync()
    {
        var tenant = await factory.SeedTenantAsync("Acme");
        var clientA = await factory.SeedClientAsync(tenant, "Client A");
        var administrator = await factory.SeedUserAsync($"operator-{Guid.NewGuid():N}", AppUserRole.Admin);
        var tenantAdministrator = await factory.SeedUserAsync($"acme-admin-{Guid.NewGuid():N}", AppUserRole.User);
        await factory.SeedMembershipAsync(tenant, tenantAdministrator, TenantRole.TenantAdministrator);
        return new Scenario(tenant, clientA, administrator, tenantAdministrator);
    }

    private sealed record Scenario(Guid Tenant, Guid ClientA, Guid Administrator, Guid TenantAdministrator);
}

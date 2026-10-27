// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MeisterDev.ProPR.Application.Features.PlatformAdminElevation;
using MeisterDev.ProPR.Application.Features.TenantData;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Features.PlatformAdminElevation;

/// <summary>
///     A platform administrator reads metadata of every tenant, and the content of a tenant only during an elevation
///     that covers it. The tests call the endpoints as the platform administrator, a tenant administrator, a tenant user
///     and the tenant machine credential of the hosted platform.
/// </summary>
public sealed class PlatformAdminContentAccessTests(ElevationApiFactory factory) : IClassFixture<ElevationApiFactory>
{
    [Fact]
    public async Task APlatformAdministratorWithoutAnElevationReadsMetadataAndIsRefusedContent()
    {
        var scenario = await this.ScenarioAsync();
        using var http = factory.ClientFor(scenario.Administrator, AppUserRole.Admin);

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/clients/{scenario.ClientA}/reviewing/history")).StatusCode);

        var detail = await ReadJsonAsync(await http.GetAsync($"/jobs/{scenario.JobA}"));
        Assert.True(detail.GetProperty("contentWithheld").GetBoolean());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("resultSummary").ValueKind);
        Assert.Equal("completed", detail.GetProperty("status").GetString());

        var failed = await ReadJsonAsync(await http.GetAsync($"/jobs/{scenario.FailedJobA}"));
        Assert.True(failed.GetProperty("contentWithheld").GetBoolean());
        Assert.Equal(JsonValueKind.Null, failed.GetProperty("errorMessage").ValueKind);

        var list = await ReadJsonAsync(await http.GetAsync($"/jobs?clientId={scenario.ClientA}"));
        var items = list.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.True(item.GetProperty("contentWithheld").GetBoolean()));
        Assert.All(items, item => Assert.Equal(JsonValueKind.Null, item.GetProperty("errorMessage").ValueKind));

        var result = await http.GetAsync($"/jobs/{scenario.JobA}/result");
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        var body = await result.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Summary of A", body, StringComparison.Ordinal);
        var refusal = JsonDocument.Parse(body).RootElement;
        Assert.Equal("elevation_required", refusal.GetProperty("code").GetString());
        Assert.Equal(scenario.Tenant, refusal.GetProperty("tenantId").GetGuid());
        Assert.Equal(scenario.ClientA, refusal.GetProperty("clientId").GetGuid());

        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"/jobs/{scenario.JobA}/protocol")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"/reviewing/jobs/{scenario.JobA}/status")).StatusCode);
        Assert.DoesNotContain(
            await factory.AuditEntriesAsync(scenario.Tenant),
            entry => entry.EventType.StartsWith("elevation.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnElevationForClientAGrantsTheContentOfClientAAndRefusesClientB()
    {
        var scenario = await this.ScenarioAsync();
        using var http = factory.ClientFor(scenario.Administrator, AppUserRole.Admin);
        await StartAsync(http, scenario.Tenant, scenario.ClientA, "TICKET-4711", 30);

        var result = await http.GetAsync($"/jobs/{scenario.JobA}/result");
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("Summary of A", (await ReadJsonAsync(result)).GetProperty("result").GetProperty("summary").GetString());

        var detail = await ReadJsonAsync(await http.GetAsync($"/jobs/{scenario.JobA}"));
        Assert.False(detail.GetProperty("contentWithheld").GetBoolean());
        Assert.Equal("Summary of A", detail.GetProperty("resultSummary").GetString());

        var other = await http.GetAsync($"/jobs/{scenario.JobB}/result");
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        Assert.Equal(scenario.ClientB, (await ReadJsonAsync(other)).GetProperty("clientId").GetGuid());
    }

    [Fact]
    public async Task AnEndedElevationRefusesContent()
    {
        var scenario = await this.ScenarioAsync();
        using var http = factory.ClientFor(scenario.Administrator, AppUserRole.Admin);
        var elevationId = (await StartAsync(http, scenario.Tenant, null, "TICKET-1", 60)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/jobs/{scenario.JobB}/result")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync($"/admin/elevations/{elevationId}/end", null)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"/jobs/{scenario.JobB}/result")).StatusCode);
    }

    [Fact]
    public async Task AnExpiredElevationRefusesContent()
    {
        var scenario = await this.ScenarioAsync();
        using var http = factory.ClientFor(scenario.Administrator, AppUserRole.Admin);
        var elevationId = (await StartAsync(http, scenario.Tenant, scenario.ClientA, "TICKET-2", 15)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/jobs/{scenario.JobA}/result")).StatusCode);

        await factory.ExpireAsync(elevationId);

        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"/jobs/{scenario.JobA}/result")).StatusCode);
    }

    [Fact]
    public async Task EveryContentReadUnderAnElevationIsInTheAuditHistoryTheTenantAdministratorReads()
    {
        var scenario = await this.ScenarioAsync();
        using var operatorHttp = factory.ClientFor(scenario.Administrator, AppUserRole.Admin);
        var elevationId = (await StartAsync(operatorHttp, scenario.Tenant, null, "TICKET-3 customer asked about a finding", 60))
            .GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await operatorHttp.GetAsync($"/jobs/{scenario.JobA}/result")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await operatorHttp.GetAsync($"/jobs/{scenario.JobB}/result")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await operatorHttp.GetAsync($"/jobs/{scenario.JobA}/result")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await operatorHttp.GetAsync($"/jobs/{scenario.JobA}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await operatorHttp.PostAsync($"/admin/elevations/{elevationId}/end", null)).StatusCode);

        using var tenantHttp = factory.ClientFor(scenario.TenantAdministrator, AppUserRole.User);
        var history = await tenantHttp.GetAsync($"/admin/tenants/{scenario.Tenant}/audit-entries");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        var reads = (await ReadJsonAsync(history)).GetProperty("items").EnumerateArray()
            .Where(entry => entry.GetProperty("eventType").GetString() == PlatformAdminElevationAuditEvents.ContentRead)
            .ToList();
        Assert.Equal(4, reads.Count);
        Assert.All(reads, read => Assert.Equal(elevationId, read.GetProperty("elevationId").GetGuid()));
        Assert.All(reads, read => Assert.Equal(scenario.Administrator, read.GetProperty("actorUserId").GetGuid()));
        Assert.All(reads, read => Assert.Contains("TICKET-3", read.GetProperty("detail").GetString(), StringComparison.Ordinal));
        Assert.Contains(
            reads,
            read => read.GetProperty("detail").GetString()!.Contains($"/jobs/{scenario.JobB}/result", StringComparison.Ordinal)
                    && read.GetProperty("summary").GetString()!.Contains("Client B", StringComparison.Ordinal));

        var operatorView = await ReadJsonAsync(await operatorHttp.GetAsync($"/admin/elevations/{elevationId}/audit-entries"));
        Assert.Equal(6, operatorView.GetArrayLength());
        var listed = await ReadJsonAsync(await operatorHttp.GetAsync($"/admin/elevations?tenantId={scenario.Tenant}"));
        Assert.Equal(4, Assert.Single(listed.GetProperty("items").EnumerateArray()).GetProperty("contentReads").GetInt32());
    }

    [Fact]
    public async Task TenantUsersReadTheContentOfTheirClientsAsBeforeAndLeaveNoElevationRecord()
    {
        var scenario = await this.ScenarioAsync();
        using var tenantHttp = factory.ClientFor(scenario.TenantAdministrator, AppUserRole.User);

        Assert.Equal(HttpStatusCode.OK, (await tenantHttp.GetAsync($"/jobs/{scenario.JobA}/result")).StatusCode);
        var detail = await ReadJsonAsync(await tenantHttp.GetAsync($"/jobs/{scenario.JobA}"));
        Assert.False(detail.GetProperty("contentWithheld").GetBoolean());
        Assert.Equal("Summary of A", detail.GetProperty("resultSummary").GetString());

        var member = await factory.SeedUserAsync($"member-{Guid.NewGuid():N}", AppUserRole.User);
        await factory.SeedMembershipAsync(scenario.Tenant, member, TenantRole.TenantUser);
        using var memberHttp = factory.ClientFor(member, AppUserRole.User);
        Assert.Equal(HttpStatusCode.Forbidden, (await memberHttp.GetAsync($"/jobs/{scenario.JobA}/result")).StatusCode);

        Assert.DoesNotContain(
            await factory.AuditEntriesAsync(scenario.Tenant),
            entry => entry.EventType.StartsWith("elevation.", StringComparison.Ordinal));
    }

    // A platform administrator can give themselves a membership in any tenant. The membership therefore grants no
    // content: the administrator needs an elevation like every other platform administrator, and each read is recorded.
    [Fact]
    public async Task APlatformAdministratorWhoIsATenantAdministratorNeedsAnElevationForTheContentOfTheTenant()
    {
        var scenario = await this.ScenarioAsync();
        var administrator = await factory.SeedUserAsync($"operator-member-{Guid.NewGuid():N}", AppUserRole.Admin);
        await factory.SeedMembershipAsync(scenario.Tenant, administrator, TenantRole.TenantAdministrator);
        using var http = factory.ClientFor(administrator, AppUserRole.Admin);

        var refused = await http.GetAsync($"/jobs/{scenario.JobA}/result");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("elevation_required", (await ReadJsonAsync(refused)).GetProperty("code").GetString());
        var detail = await ReadJsonAsync(await http.GetAsync($"/jobs/{scenario.JobA}"));
        Assert.True(detail.GetProperty("contentWithheld").GetBoolean());

        var elevationId = (await StartAsync(http, scenario.Tenant, scenario.ClientA, "TICKET-8", 20)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/jobs/{scenario.JobA}/result")).StatusCode);
        var read = Assert.Single(
            await factory.AuditEntriesAsync(scenario.Tenant),
            entry => entry.EventType == PlatformAdminElevationAuditEvents.ContentRead);
        Assert.Equal(elevationId, read.ElevationId);
        Assert.Equal(administrator, read.ActorUserId);
    }

    // Model binding gives the action the first of several values. Without the refusal, a repeated clientId that the
    // filter could not read let the action return the content of that client without a grant and without a record.
    [Fact]
    public async Task AClientIdNamedMoreThanOnceIsRefusedAndTheContentIsNotRead()
    {
        var scenario = await this.ScenarioAsync();
        using var http = factory.ClientFor(scenario.Administrator, AppUserRole.Admin);
        await StartAsync(http, scenario.Tenant, scenario.ClientB, "TICKET-12", 30);

        var repeated = await http.GetAsync($"/admin/thread-memory?clientId={scenario.ClientA}&clientId={scenario.ClientA}");
        var mixed = await http.GetAsync($"/admin/thread-memory?clientId={scenario.ClientA}&clientId={scenario.ClientB}");

        Assert.Equal(HttpStatusCode.BadRequest, repeated.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, mixed.StatusCode);
        await this.AssertThreadMemoryNotReadAsync(scenario.ClientA);
        await this.AssertThreadMemoryNotReadAsync(scenario.ClientB);
        Assert.DoesNotContain(
            await factory.AuditEntriesAsync(scenario.Tenant),
            entry => entry.EventType == PlatformAdminElevationAuditEvents.ContentRead);
    }

    // The elevation covers every client of the tenant, so the refusal can only come from the identifier in the request.
    // For the thread memory, model binding and the action refuse these identifiers as well. On the client route and on
    // the review job route, the action answers 404 for an empty identifier, so the 400 on these routes comes from the
    // content filter.
    [Theory]
    [InlineData("/admin/thread-memory?clientId=not-a-client")]
    [InlineData("/admin/thread-memory?clientId=00000000-0000-0000-0000-000000000000")]
    [InlineData("/admin/thread-memory")]
    [InlineData("/clients/00000000-0000-0000-0000-000000000000/reviewing/jobs/{jobA}/status")]
    [InlineData("/jobs/00000000-0000-0000-0000-000000000000/result")]
    public async Task AContentRequestWithoutAReadableClientIsRefusedDuringAnElevationForTheWholeTenant(string path)
    {
        var scenario = await this.ScenarioAsync();
        using var http = factory.ClientFor(scenario.Administrator, AppUserRole.Admin);
        await StartAsync(http, scenario.Tenant, null, "TICKET-16", 30);

        var response = await http.GetAsync(path.Replace("{jobA}", scenario.JobA.ToString(), StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("Summary of A", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await this.AssertThreadMemoryNotReadAsync(Guid.Empty);
        await this.AssertThreadMemoryNotReadAsync(scenario.ClientA);
        await this.AssertThreadMemoryNotReadAsync(scenario.ClientB);
        Assert.DoesNotContain(
            await factory.AuditEntriesAsync(scenario.Tenant),
            entry => entry.EventType == PlatformAdminElevationAuditEvents.ContentRead);
    }

    [Fact]
    public async Task AContentRequestForAClientOrAReviewJobThatDoesNotExistIsNotFound()
    {
        var scenario = await this.ScenarioAsync();
        using var http = factory.ClientFor(scenario.Administrator, AppUserRole.Admin);
        var unknownClient = Guid.NewGuid();

        var threadMemory = await http.GetAsync($"/admin/thread-memory?clientId={unknownClient}");
        var status = await http.GetAsync($"/clients/{unknownClient}/reviewing/jobs/{scenario.JobA}/status");
        var result = await http.GetAsync($"/jobs/{Guid.NewGuid()}/result");

        Assert.Equal(HttpStatusCode.NotFound, threadMemory.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, status.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        await this.AssertThreadMemoryNotReadAsync(unknownClient);
    }

    [Fact]
    public async Task TheThreadMemoryOfTheClientAnElevationCoversIsReadAndRecorded()
    {
        var scenario = await this.ScenarioAsync();
        using var http = factory.ClientFor(scenario.Administrator, AppUserRole.Admin);
        var elevationId = (await StartAsync(http, scenario.Tenant, scenario.ClientA, "TICKET-13", 30)).GetProperty("id").GetGuid();

        var response = await http.GetAsync($"/admin/thread-memory?clientId={scenario.ClientA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await factory.ThreadMemory.Received(1).GetPagedAsync(
            scenario.ClientA,
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<MemorySource?>(),
            Arg.Any<string?>(),
            Arg.Any<int?>(),
            Arg.Any<CancellationToken>());
        var read = Assert.Single(
            await factory.AuditEntriesAsync(scenario.Tenant),
            entry => entry.EventType == PlatformAdminElevationAuditEvents.ContentRead);
        Assert.Equal(elevationId, read.ElevationId);
    }

    [Fact]
    public async Task TheContentOfTheSystemTenantNeedsNoElevation()
    {
        var scenario = await this.ScenarioAsync();
        var systemClient = await factory.SeedClientAsync(TenantCatalog.SystemTenantId, "Installation client");
        var systemJob = await factory.SeedCompletedJobAsync(systemClient, "Summary of the installation");
        using var http = factory.ClientFor(scenario.Administrator, AppUserRole.Admin);

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/jobs/{systemJob}/result")).StatusCode);
        var detail = await ReadJsonAsync(await http.GetAsync($"/jobs/{systemJob}"));
        Assert.False(detail.GetProperty("contentWithheld").GetBoolean());
    }

    [Fact]
    public async Task TheDataRequestsOfTheHostedPlatformRunWithoutAnElevation()
    {
        var scenario = await this.ScenarioAsync();
        factory.Lifecycle.WriteExportAsync(scenario.Tenant, Arg.Any<Stream>(), Arg.Any<TenantExportOptions>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await call.ArgAt<Stream>(1).WriteAsync("{\"format\":\"propr.tenant-export\"}"u8.ToArray());
                return true;
            });
        factory.Lifecycle.EraseAuthorAsync(scenario.Tenant, Arg.Any<AuthorIdentity>(), Arg.Any<Guid?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new IdentityErasureResult(1, 0, 0, 2));
        factory.Lifecycle.DeleteTenantAsync(scenario.Tenant, Arg.Any<CancellationToken>())
            .Returns(new TenantDeletionAnswer(new TenantDeletionResult(scenario.Tenant, new Dictionary<string, int>()), null));
        using var http = factory.ClientFor(scenario.Administrator, AppUserRole.Admin);

        var export = await http.GetAsync($"/admin/tenants/{scenario.Tenant}/export?includeProtocols=true");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Contains("propr.tenant-export", await export.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var erasure = await http.PostAsJsonAsync(
            $"/admin/tenants/{scenario.Tenant}/identity-erasures",
            new { externalUserId = "dev-1", login = "dev", instruction = "DR-17" });
        Assert.Equal(HttpStatusCode.OK, erasure.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await http.DeleteAsync($"/admin/tenants/{scenario.Tenant}")).StatusCode);
        Assert.DoesNotContain(
            await factory.AuditEntriesAsync(scenario.Tenant),
            entry => entry.EventType.StartsWith("elevation.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheTenantMachineCredentialOfTheHostedPlatformReadsReviewsWithoutAnElevation()
    {
        var scenario = await this.ScenarioAsync();
        using var http = factory.ClientForMachine(await factory.IssueMachineTokenAsync(scenario.Tenant));

        var status = await http.GetAsync($"/clients/{scenario.ClientA}/reviewing/jobs/{scenario.JobA}/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Contains("Summary of A", await status.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/clients/{scenario.ClientA}/reviewing/history")).StatusCode);
        Assert.DoesNotContain(
            await factory.AuditEntriesAsync(scenario.Tenant),
            entry => entry.EventType.StartsWith("elevation.", StringComparison.Ordinal));
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

    private Task AssertThreadMemoryNotReadAsync(Guid clientId) =>
        factory.ThreadMemory.DidNotReceive().GetPagedAsync(
            clientId,
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<MemorySource?>(),
            Arg.Any<string?>(),
            Arg.Any<int?>(),
            Arg.Any<CancellationToken>());

    private async Task<Scenario> ScenarioAsync()
    {
        var tenant = await factory.SeedTenantAsync("Acme");
        var clientA = await factory.SeedClientAsync(tenant, "Client A");
        var clientB = await factory.SeedClientAsync(tenant, "Client B");
        var jobA = await factory.SeedCompletedJobAsync(clientA, "Summary of A");
        var failedJobA = await factory.SeedFailedJobAsync(clientA, "Error that quotes the code of A");
        var jobB = await factory.SeedCompletedJobAsync(clientB, "Summary of B");
        var administrator = await factory.SeedUserAsync($"operator-{Guid.NewGuid():N}", AppUserRole.Admin);
        var tenantAdministrator = await factory.SeedUserAsync($"acme-admin-{Guid.NewGuid():N}", AppUserRole.User);
        await factory.SeedMembershipAsync(tenant, tenantAdministrator, TenantRole.TenantAdministrator);
        return new Scenario(tenant, clientA, clientB, jobA, failedJobA, jobB, administrator, tenantAdministrator);
    }

    private sealed record Scenario(
        Guid Tenant,
        Guid ClientA,
        Guid ClientB,
        Guid JobA,
        Guid FailedJobA,
        Guid JobB,
        Guid Administrator,
        Guid TenantAdministrator);
}

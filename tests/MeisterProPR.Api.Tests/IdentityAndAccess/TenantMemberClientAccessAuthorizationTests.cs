// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Api.Tests.IdentityAndAccess;

public sealed class TenantMemberClientAccessAuthorizationTests(TenantAdministrationApiFactory factory)
    : IClassFixture<TenantAdministrationApiFactory>
{
    [Fact]
    public async Task ListTenantClients_TenantAdministrator_ReturnsTenantClients()
    {
        var context = await this.SeedTenantWithAdminAndMemberAsync();
        await factory.SeedClientAsync(context.TenantId, "Client A");
        await factory.SeedClientAsync(context.TenantId, "Client B");

        var response = await this.SendAsync(HttpMethod.Get, $"/admin/tenants/{context.TenantId}/clients", context.AdminId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<List<TenantClientSummary>>();
        Assert.NotNull(body);
        Assert.Equal(2, body!.Count);
    }

    [Fact]
    public async Task AssignThenList_TenantAdministrator_GrantsAndReflectsAccess()
    {
        var context = await this.SeedTenantWithAdminAndMemberAsync();
        var clientId = await factory.SeedClientAsync(context.TenantId, "Assignable Client");

        var assignResponse = await this.SendAsync(
            HttpMethod.Post,
            $"/admin/tenants/{context.TenantId}/memberships/{context.MemberMembershipId}/clients",
            context.AdminId,
            new { clientId, role = ClientRole.ClientUser.ToString() });

        Assert.Equal(HttpStatusCode.OK, assignResponse.StatusCode);
        var assignment = await assignResponse.Content.ReadFromJsonAsync<MemberClientAccess>();
        Assert.NotNull(assignment);
        Assert.Equal(clientId, assignment!.ClientId);
        Assert.Equal("clientUser", assignment.Role);

        var listResponse = await this.SendAsync(
            HttpMethod.Get,
            $"/admin/tenants/{context.TenantId}/memberships/{context.MemberMembershipId}/clients",
            context.AdminId);

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var assignments = await listResponse.Content.ReadFromJsonAsync<List<MemberClientAccess>>();
        Assert.NotNull(assignments);
        Assert.Single(assignments!);
        Assert.Equal(clientId, assignments![0].ClientId);
    }

    [Fact]
    public async Task RemoveAccess_TenantAdministrator_Returns204AndRevokes()
    {
        var context = await this.SeedTenantWithAdminAndMemberAsync();
        var clientId = await factory.SeedClientAsync(context.TenantId, "Revocable Client");
        await this.SendAsync(
            HttpMethod.Post,
            $"/admin/tenants/{context.TenantId}/memberships/{context.MemberMembershipId}/clients",
            context.AdminId,
            new { clientId, role = ClientRole.ClientUser.ToString() });

        var deleteResponse = await this.SendAsync(
            HttpMethod.Delete,
            $"/admin/tenants/{context.TenantId}/memberships/{context.MemberMembershipId}/clients/{clientId}",
            context.AdminId);

        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var listResponse = await this.SendAsync(
            HttpMethod.Get,
            $"/admin/tenants/{context.TenantId}/memberships/{context.MemberMembershipId}/clients",
            context.AdminId);
        var assignments = await listResponse.Content.ReadFromJsonAsync<List<MemberClientAccess>>();
        Assert.NotNull(assignments);
        Assert.Empty(assignments!);
    }

    [Fact]
    public async Task Assign_ClientOutsideTenant_Returns400()
    {
        var context = await this.SeedTenantWithAdminAndMemberAsync();
        var otherTenantId = await factory.SeedTenantAsync($"globex-{Guid.NewGuid():N}", "Globex Corp");
        var foreignClientId = await factory.SeedClientAsync(otherTenantId, "Foreign Client");

        var response = await this.SendAsync(
            HttpMethod.Post,
            $"/admin/tenants/{context.TenantId}/memberships/{context.MemberMembershipId}/clients",
            context.AdminId,
            new { clientId = foreignClientId, role = ClientRole.ClientUser.ToString() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Assign_MembershipNotInTenant_Returns404()
    {
        var context = await this.SeedTenantWithAdminAndMemberAsync();
        var clientId = await factory.SeedClientAsync(context.TenantId, "Client");

        var response = await this.SendAsync(
            HttpMethod.Post,
            $"/admin/tenants/{context.TenantId}/memberships/{Guid.NewGuid()}/clients",
            context.AdminId,
            new { clientId, role = ClientRole.ClientUser.ToString() });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Assign_NonAdministratorTenantMember_Returns403()
    {
        var context = await this.SeedTenantWithAdminAndMemberAsync();
        var clientId = await factory.SeedClientAsync(context.TenantId, "Client");

        var response = await this.SendAsync(
            HttpMethod.Post,
            $"/admin/tenants/{context.TenantId}/memberships/{context.MemberMembershipId}/clients",
            context.MemberId,
            new { clientId, role = ClientRole.ClientUser.ToString() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Assign_AdministratorOfDifferentTenant_Returns403()
    {
        var context = await this.SeedTenantWithAdminAndMemberAsync();
        var otherTenantId = await factory.SeedTenantAsync($"globex-{Guid.NewGuid():N}", "Globex Corp");
        var otherAdminId = await factory.SeedUserAsync($"other.admin.{Guid.NewGuid():N}", $"other.admin.{Guid.NewGuid():N}@globex.test");
        await factory.SeedTenantMembershipAsync(otherTenantId, otherAdminId, TenantRole.TenantAdministrator);
        var clientId = await factory.SeedClientAsync(context.TenantId, "Client");

        var response = await this.SendAsync(
            HttpMethod.Post,
            $"/admin/tenants/{context.TenantId}/memberships/{context.MemberMembershipId}/clients",
            otherAdminId,
            new { clientId, role = ClientRole.ClientUser.ToString() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ListTenantClients_Unauthenticated_Returns401()
    {
        var tenantId = await factory.SeedTenantAsync($"acme-{Guid.NewGuid():N}", "Acme Corp");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/admin/tenants/{tenantId}/clients");
        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<SeededTenantContext> SeedTenantWithAdminAndMemberAsync()
    {
        var tenantId = await factory.SeedTenantAsync($"acme-{Guid.NewGuid():N}", "Acme Corp");
        var adminId = await factory.SeedUserAsync($"tenant.admin.{Guid.NewGuid():N}", $"tenant.admin.{Guid.NewGuid():N}@acme.test");
        await factory.SeedTenantMembershipAsync(tenantId, adminId, TenantRole.TenantAdministrator);
        var memberId = await factory.SeedUserAsync($"tenant.user.{Guid.NewGuid():N}", $"tenant.user.{Guid.NewGuid():N}@acme.test");
        var memberMembershipId = await factory.SeedTenantMembershipAsync(tenantId, memberId, TenantRole.TenantUser);

        return new SeededTenantContext(tenantId, adminId, memberId, memberMembershipId);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string requestUri,
        Guid actingUserId,
        object? body = null)
    {
        using var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(actingUserId, AppUserRole.User));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await factory.CreateClient().SendAsync(request);
    }

    private sealed record SeededTenantContext(Guid TenantId, Guid AdminId, Guid MemberId, Guid MemberMembershipId);

    private sealed record TenantClientSummary(Guid Id, string DisplayName, bool IsActive);

    private sealed record MemberClientAccess(Guid ClientId, string ClientDisplayName, string Role, DateTimeOffset AssignedAt);
}

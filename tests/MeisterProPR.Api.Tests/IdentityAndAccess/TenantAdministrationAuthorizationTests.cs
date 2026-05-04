// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Api.Tests.IdentityAndAccess;

public sealed class TenantAdministrationAuthorizationTests(TenantAdministrationApiFactory factory)
    : IClassFixture<TenantAdministrationApiFactory>
{
    [Fact]
    public async Task PostMembership_TenantAdministratorForOwnTenant_Returns405()
    {
        var tenantId = await factory.SeedTenantAsync($"acme-{Guid.NewGuid():N}", "Acme Corp");
        var tenantAdminId = await factory.SeedUserAsync($"tenant.admin.{Guid.NewGuid():N}", $"tenant.admin.{Guid.NewGuid():N}@acme.test");
        var tenantUserId = await factory.SeedUserAsync($"tenant.user.{Guid.NewGuid():N}", $"tenant.user.{Guid.NewGuid():N}@acme.test");
        await factory.SeedTenantMembershipAsync(tenantId, tenantAdminId, TenantRole.TenantAdministrator);

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/tenants/{tenantId}/memberships");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(tenantAdminId, AppUserRole.User));
        request.Content = JsonContent.Create(new { userIdentifier = tenantUserId.ToString(), role = TenantRole.TenantUser.ToString() });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task PostMembership_TenantAdministratorForOtherTenant_Returns405()
    {
        var ownTenantId = await factory.SeedTenantAsync($"acme-{Guid.NewGuid():N}", "Acme Corp");
        var otherTenantId = await factory.SeedTenantAsync($"globex-{Guid.NewGuid():N}", "Globex Corp");
        var tenantAdminId = await factory.SeedUserAsync($"tenant.admin.{Guid.NewGuid():N}", $"tenant.admin.{Guid.NewGuid():N}@acme.test");
        var tenantUserId = await factory.SeedUserAsync($"tenant.user.{Guid.NewGuid():N}", $"tenant.user.{Guid.NewGuid():N}@globex.test");
        await factory.SeedTenantMembershipAsync(ownTenantId, tenantAdminId, TenantRole.TenantAdministrator);

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/tenants/{otherTenantId}/memberships");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(tenantAdminId, AppUserRole.User));
        request.Content = JsonContent.Create(new { userIdentifier = tenantUserId.ToString(), role = TenantRole.TenantUser.ToString() });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task PatchMembership_DemotingLastTenantAdministrator_Returns409()
    {
        var tenantId = await factory.SeedTenantAsync($"acme-{Guid.NewGuid():N}", "Acme Corp");
        var tenantAdminId = await factory.SeedUserAsync($"tenant.admin.{Guid.NewGuid():N}", $"tenant.admin.{Guid.NewGuid():N}@acme.test");
        var membershipId = await factory.SeedTenantMembershipAsync(tenantId, tenantAdminId, TenantRole.TenantAdministrator);

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/admin/tenants/{tenantId}/memberships/{membershipId}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(tenantAdminId, AppUserRole.User));
        request.Content = JsonContent.Create(new { role = TenantRole.TenantUser.ToString() });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task DeleteMembership_RemovingLastTenantAdministrator_Returns409()
    {
        var tenantId = await factory.SeedTenantAsync($"acme-{Guid.NewGuid():N}", "Acme Corp");
        var tenantAdminId = await factory.SeedUserAsync($"tenant.admin.{Guid.NewGuid():N}", $"tenant.admin.{Guid.NewGuid():N}@acme.test");
        var membershipId = await factory.SeedTenantMembershipAsync(tenantId, tenantAdminId, TenantRole.TenantAdministrator);

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/tenants/{tenantId}/memberships/{membershipId}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(tenantAdminId, AppUserRole.User));

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task ListUsers_TenantAdministrator_Returns403()
    {
        var tenantId = await factory.SeedTenantAsync($"acme-{Guid.NewGuid():N}", "Acme Corp");
        var tenantAdminId = await factory.SeedUserAsync($"tenant.admin.{Guid.NewGuid():N}", $"tenant.admin.{Guid.NewGuid():N}@acme.test");
        await factory.SeedTenantMembershipAsync(tenantId, tenantAdminId, TenantRole.TenantAdministrator);

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/users");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(tenantAdminId, AppUserRole.User));

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetClient_WithClientAssignmentButWithoutTenantMembership_Returns403()
    {
        var tenantId = await factory.SeedTenantAsync($"acme-{Guid.NewGuid():N}", "Acme Corp");
        var userId = await factory.SeedUserAsync($"client.admin.{Guid.NewGuid():N}", $"client.admin.{Guid.NewGuid():N}@acme.test");
        var clientId = await factory.SeedClientAsync(tenantId, "Acme Client");
        await factory.SeedClientAssignmentAsync(userId, clientId, ClientRole.ClientAdministrator);

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/clients/{clientId}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(userId, AppUserRole.User));

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetClients_WithAssignmentsAcrossTenants_ReturnsOnlyClientsForVisibleTenantMemberships()
    {
        var ownTenantId = await factory.SeedTenantAsync($"acme-{Guid.NewGuid():N}", "Acme Corp");
        var otherTenantId = await factory.SeedTenantAsync($"globex-{Guid.NewGuid():N}", "Globex Corp");
        var userId = await factory.SeedUserAsync($"tenant.user.{Guid.NewGuid():N}", $"tenant.user.{Guid.NewGuid():N}@acme.test");
        await factory.SeedTenantMembershipAsync(ownTenantId, userId, TenantRole.TenantUser);

        var ownClientId = await factory.SeedClientAsync(ownTenantId, "Acme Client");
        var otherClientId = await factory.SeedClientAsync(otherTenantId, "Globex Client");
        await factory.SeedClientAssignmentAsync(userId, ownClientId, ClientRole.ClientAdministrator);
        await factory.SeedClientAssignmentAsync(userId, otherClientId, ClientRole.ClientAdministrator);

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/clients");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(userId, AppUserRole.User));

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<List<ClientListItemResponse>>();
        Assert.NotNull(body);
        Assert.Single(body!);
        Assert.Equal(ownClientId, body[0].Id);
        Assert.Equal("Acme Client", body[0].DisplayName);
    }

    [Fact]
    public async Task GetClients_TenantUserWithoutExplicitAssignment_ReturnsTenantScopedClients()
    {
        var tenantId = await factory.SeedTenantAsync($"acme-{Guid.NewGuid():N}", "Acme Corp");
        var userId = await factory.SeedUserAsync($"tenant.user.{Guid.NewGuid():N}", $"tenant.user.{Guid.NewGuid():N}@acme.test");
        await factory.SeedTenantMembershipAsync(tenantId, userId, TenantRole.TenantUser);

        var visibleClientId = await factory.SeedClientAsync(tenantId, "Tenant Visible Client");

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/clients");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(userId, AppUserRole.User));

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<List<ClientListItemResponse>>();
        Assert.NotNull(body);
        Assert.Single(body!);
        Assert.Equal(visibleClientId, body[0].Id);
        Assert.Equal("Tenant Visible Client", body[0].DisplayName);
    }

    [Fact]
    public async Task PostClients_TenantAdministratorForOwnTenant_Returns201()
    {
        var tenantId = await factory.SeedTenantAsync($"acme-{Guid.NewGuid():N}", "Acme Corp");
        var tenantAdminId = await factory.SeedUserAsync($"tenant.admin.{Guid.NewGuid():N}", $"tenant.admin.{Guid.NewGuid():N}@acme.test");
        await factory.SeedTenantMembershipAsync(tenantId, tenantAdminId, TenantRole.TenantAdministrator);

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/clients");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(tenantAdminId, AppUserRole.User));
        request.Content = JsonContent.Create(new { displayName = "Tenant Admin Client", tenantId });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task PostClients_TenantUser_Returns403()
    {
        var tenantId = await factory.SeedTenantAsync($"acme-{Guid.NewGuid():N}", "Acme Corp");
        var tenantUserId = await factory.SeedUserAsync($"tenant.user.{Guid.NewGuid():N}", $"tenant.user.{Guid.NewGuid():N}@acme.test");
        await factory.SeedTenantMembershipAsync(tenantId, tenantUserId, TenantRole.TenantUser);

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/clients");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(tenantUserId, AppUserRole.User));
        request.Content = JsonContent.Create(new { displayName = "Tenant User Client", tenantId });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AuthLogin_PlatformAdminRecoveryStillWorks_WhenTenantDisablesLocalLogin()
    {
        await factory.SeedTenantAsync($"acme-{Guid.NewGuid():N}", "Acme Corp", false);
        var platformAdminId = await factory.SeedUserAsync(
            $"platform.admin.{Guid.NewGuid():N}",
            $"platform.admin.{Guid.NewGuid():N}@meister.test",
            AppUserRole.Admin);
        await factory.SetLocalPasswordAsync(platformAdminId, "CorrectPassword1!");

        var response = await factory.CreateClient()
            .PostAsJsonAsync(
                "/auth/login",
                new { username = await factory.GetUsernameAsync(platformAdminId), password = "CorrectPassword1!" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed record ClientListItemResponse(Guid Id, string DisplayName);
}

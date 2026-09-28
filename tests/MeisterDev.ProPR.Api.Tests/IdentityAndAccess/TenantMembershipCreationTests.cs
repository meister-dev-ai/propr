// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Api.Tests.IdentityAndAccess;

public sealed class TenantMembershipCreationTests(TenantAdministrationApiFactory factory)
    : IClassFixture<TenantAdministrationApiFactory>
{
    [Fact]
    public async Task PostMembership_PlatformAdmin_CreatesMembershipForExistingUser()
    {
        var tenantId = await factory.SeedTenantAsync($"tenant-{Guid.NewGuid():N}", "Tenant");
        var userId = await factory.SeedUserAsync($"user-{Guid.NewGuid():N}", $"user-{Guid.NewGuid():N}@example.test");
        var client = factory.CreateClient();
        using var request = CreateRequest(factory, tenantId, userId, TenantRole.TenantAdministrator);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var membership = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(tenantId, membership.GetProperty("tenantId").GetGuid());
        Assert.Equal(userId, membership.GetProperty("userId").GetGuid());
        Assert.Equal("tenantAdministrator", membership.GetProperty("role").GetString());
        Assert.EndsWith($"/admin/tenants/{tenantId}/memberships/{membership.GetProperty("id").GetGuid()}", response.Headers.Location?.ToString());

        using var get = new HttpRequestMessage(HttpMethod.Get, response.Headers.Location);
        get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateToken(Guid.NewGuid(), AppUserRole.Admin));
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(get)).StatusCode);
    }

    [Fact]
    public async Task PostMembership_Duplicate_Returns409WithoutChangingRole()
    {
        var tenantId = await factory.SeedTenantAsync($"tenant-{Guid.NewGuid():N}", "Tenant");
        var userId = await factory.SeedUserAsync($"user-{Guid.NewGuid():N}", $"user-{Guid.NewGuid():N}@example.test");
        await factory.SeedTenantMembershipAsync(tenantId, userId, TenantRole.TenantUser);
        var client = factory.CreateClient();
        using var request = CreateRequest(factory, tenantId, userId, TenantRole.TenantAdministrator);

        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(request)).StatusCode);

        using var list = new HttpRequestMessage(HttpMethod.Get, $"/admin/tenants/{tenantId}/memberships");
        list.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateToken(Guid.NewGuid(), AppUserRole.Admin));
        var memberships = JsonDocument.Parse(await (await client.SendAsync(list)).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("tenantUser", Assert.Single(memberships.EnumerateArray()).GetProperty("role").GetString());
    }

    [Fact]
    public async Task PostMembership_MissingUser_Returns404()
    {
        var tenantId = await factory.SeedTenantAsync($"tenant-{Guid.NewGuid():N}", "Tenant");
        using var request = CreateRequest(factory, tenantId, Guid.NewGuid(), TenantRole.TenantUser);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task PostMembership_InvalidRole_Returns400WithoutCreatingMembership()
    {
        var tenantId = await factory.SeedTenantAsync($"tenant-{Guid.NewGuid():N}", "Tenant");
        var userId = await factory.SeedUserAsync($"user-{Guid.NewGuid():N}", $"user-{Guid.NewGuid():N}@example.test");
        using var request = CreateRequest(factory, tenantId, userId, TenantRole.TenantUser);
        request.Content = JsonContent.Create(new { userId, role = "42" });

        Assert.Equal(HttpStatusCode.BadRequest, (await factory.CreateClient().SendAsync(request)).StatusCode);

        using var list = new HttpRequestMessage(HttpMethod.Get, $"/admin/tenants/{tenantId}/memberships");
        list.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateToken(Guid.NewGuid(), AppUserRole.Admin));
        var memberships = JsonDocument.Parse(await (await factory.CreateClient().SendAsync(list)).Content.ReadAsStringAsync()).RootElement;
        Assert.Empty(memberships.EnumerateArray());
    }

    private static HttpRequestMessage CreateRequest(
        TenantAdministrationApiFactory factory,
        Guid tenantId,
        Guid userId,
        TenantRole role)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/admin/tenants/{tenantId}/memberships");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateToken(Guid.NewGuid(), AppUserRole.Admin));
        request.Content = JsonContent.Create(new { userId, role = role.ToString() });
        return request;
    }
}

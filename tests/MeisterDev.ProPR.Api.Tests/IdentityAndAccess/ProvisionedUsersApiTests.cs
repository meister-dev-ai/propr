// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.IdentityAndAccess;

/// <summary>
///     The provisioning endpoints through which a hosting platform keeps the users of its customers on the installation:
///     one user per subject, one membership per tenant with client-user access to the clients the platform names, and
///     the end of all access when the platform removes the last membership.
/// </summary>
public sealed class ProvisionedUsersApiTests(ProvisionedUserApiFactory factory) : IClassFixture<ProvisionedUserApiFactory>
{
    public static TheoryData<string, string> ProvisioningRequests => new()
    {
        { "PUT", "/admin/provisioned-users/{newSubject}" },
        { "GET", "/admin/tenants/{tenantId}/provisioned-members" },
        { "PUT", "/admin/tenants/{tenantId}/provisioned-members/{subject}" },
        { "DELETE", "/admin/tenants/{tenantId}/provisioned-members/{subject}" },
    };

    [Fact]
    public async Task PutUser_CreatesAnInactiveAccountWithoutPasswordAndWithoutEmailAddress()
    {
        this.Reset();
        using var platform = await this.PlatformAdministratorAsync();
        var subject = NewSubject();

        var response = await PutUserAsync(platform, subject, $"{subject}@customer.test", "Ada Customer");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(subject, body.GetProperty("subject").GetString());
        Assert.False(body.GetProperty("isActive").GetBoolean());
        var (account, provisioned) = await this.ReadAsync(subject);
        Assert.Equal(body.GetProperty("userId").GetGuid(), account.Id);
        Assert.Equal(AppUserRole.User, account.GlobalRole);
        Assert.False(account.IsActive);
        Assert.Null(account.PasswordHash);
        Assert.Null(account.Email);
        Assert.Null(account.NormalizedEmail);
        Assert.Equal(ProvisionedUserApiFactory.Authority, provisioned.Issuer);
        Assert.Equal($"{subject}@customer.test", provisioned.Email);
        Assert.Equal("Ada Customer", provisioned.DisplayName);
        factory.AuditLog.Received(1).ProvisionedUserCreated(
            Arg.Any<Guid>(),
            account.Id,
            ProvisionedUserApiFactory.Authority);
    }

    [Fact]
    public async Task PutUser_ForAProvisionedSubject_UpdatesTheSameAccount()
    {
        this.Reset();
        using var platform = await this.PlatformAdministratorAsync();
        var subject = NewSubject();
        var first = await ReadJsonAsync(await PutUserAsync(platform, subject, $"{subject}@customer.test", "Ada"));

        var second = await PutUserAsync(platform, subject, $"renamed-{subject}@customer.test", "Ada Lovelace");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(first.GetProperty("userId").GetGuid(), (await ReadJsonAsync(second)).GetProperty("userId").GetGuid());
        var (_, provisioned) = await this.ReadAsync(subject);
        Assert.Equal($"renamed-{subject}@customer.test", provisioned.Email);
        Assert.Equal("Ada Lovelace", provisioned.DisplayName);
        Assert.Equal(1, await this.CountProvisionedAsync(subject));
    }

    [Theory]
    [InlineData("   ", "person@customer.test")]
    [InlineData("subject\nwith-a-line-break", "person@customer.test")]
    [InlineData("customer-subject", "no-at-sign")]
    [InlineData("customer-subject", "two@at@signs.test")]
    [InlineData("customer-subject", "space in@customer.test")]
    public async Task PutUser_WithASubjectOrEmailAddressThatIsNotAccepted_IsRefused(string subject, string email)
    {
        this.Reset();
        using var platform = await this.PlatformAdministratorAsync();

        var response = await PutUserAsync(platform, subject, email, null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", (await ReadJsonAsync(response)).GetProperty("error").GetString());
        Assert.Equal(0, await this.CountProvisionedAsync(subject));
    }

    [Fact]
    public async Task PutMembership_GivesTheUserTheTenantUserRoleAndClientUserAccessToExactlyTheGivenClients()
    {
        this.Reset();
        using var platform = await this.PlatformAdministratorAsync();
        var tenant = await this.TenantWithClientsAsync(3);
        var subject = NewSubject();
        var userId = await this.ProvisionAsync(platform, subject);

        var first = await PutMembershipAsync(platform, tenant.Id, subject, tenant.Clients[0], tenant.Clients[1]);
        var second = await PutMembershipAsync(platform, tenant.Id, subject, tenant.Clients[1], tenant.Clients[2]);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await ReadJsonAsync(second);
        Assert.True(body.GetProperty("isActive").GetBoolean());
        Assert.Equal("tenantUser", body.GetProperty("role").GetString());
        Assert.Equal(Sorted(tenant.Clients[1], tenant.Clients[2]), ClientIdsOf(body));
        using var person = factory.CreateClientWithAccessToken(factory.GenerateToken(userId, AppUserRole.User));
        var me = await ReadJsonAsync(await person.GetAsync("/auth/me"));
        Assert.Equal([tenant.Id.ToString()], RoleKeys(me, "tenantRoles"));
        Assert.Equal((int)TenantRole.TenantUser, me.GetProperty("tenantRoles").GetProperty(tenant.Id.ToString()).GetInt32());
        Assert.Equal(
            new[] { tenant.Clients[1], tenant.Clients[2] }.Select(id => id.ToString()).Order(StringComparer.Ordinal),
            RoleKeys(me, "clientRoles"));
        Assert.All(
            me.GetProperty("clientRoles").EnumerateObject(),
            role => Assert.Equal((int)ClientRole.ClientUser, role.Value.GetInt32()));
    }

    /// <summary>
    ///     An administrator of the installation raised the membership to tenant administrator and the role on a client to
    ///     client administrator. The platform decides the roles of its members, so the next membership it sets brings both
    ///     back to the roles of a member.
    /// </summary>
    [Fact]
    public async Task PutMembership_SetsTheRolesThatThePlatformDecides()
    {
        this.Reset();
        using var platform = await this.PlatformAdministratorAsync();
        var tenant = await this.TenantWithClientsAsync(1);
        var subject = NewSubject();
        var userId = await this.ProvisionAsync(platform, subject);
        Assert.Equal(HttpStatusCode.OK, (await PutMembershipAsync(platform, tenant.Id, subject, tenant.Clients[0])).StatusCode);
        await factory.SeedTenantMembershipAsync(tenant.Id, userId, TenantRole.TenantAdministrator);
        await this.SetClientRoleAsync(userId, tenant.Clients[0], ClientRole.ClientAdministrator);

        var response = await PutMembershipAsync(platform, tenant.Id, subject, tenant.Clients[0]);

        Assert.Equal("tenantUser", (await ReadJsonAsync(response)).GetProperty("role").GetString());
        await using var scope = this.Database(out var database);
        Assert.Equal(
            TenantRole.TenantUser,
            (await database.TenantMemberships.AsNoTracking().SingleAsync(m => m.UserId == userId && m.TenantId == tenant.Id)).Role);
        Assert.Equal(
            ClientRole.ClientUser,
            (await database.UserClientRoles.AsNoTracking().SingleAsync(r => r.UserId == userId)).Role);
    }

    [Fact]
    public async Task PutMembership_WritesEachChangeToTheAuditHistoryOfTheTenantOnce()
    {
        this.Reset();
        var administrator = await this.PlatformAdministratorWithIdAsync();
        using var platform = administrator.Client;
        var tenant = await this.TenantWithClientsAsync(1);
        var subject = NewSubject();
        await this.ProvisionAsync(platform, subject);

        await PutMembershipAsync(platform, tenant.Id, subject, tenant.Clients[0]);
        await PutMembershipAsync(platform, tenant.Id, subject, tenant.Clients[0]);

        await using var scope = this.Database(out var database);
        var entries = await database.TenantAuditEntries.AsNoTracking().Where(entry => entry.TenantId == tenant.Id).ToListAsync();
        Assert.Equal(
            ["tenant.client-access.assigned", "tenant.membership.assigned"],
            entries.Select(entry => entry.EventType).Order(StringComparer.Ordinal));
        Assert.All(entries, entry => Assert.Equal(administrator.UserId, entry.ActorUserId));
    }

    [Fact]
    public async Task PutMembership_WithAClientOfAnotherTenant_IsRefusedAndChangesNothing()
    {
        this.Reset();
        using var platform = await this.PlatformAdministratorAsync();
        var tenant = await this.TenantWithClientsAsync(1);
        var otherTenant = await this.TenantWithClientsAsync(1);
        var subject = NewSubject();
        var userId = await this.ProvisionAsync(platform, subject);

        var response = await PutMembershipAsync(platform, tenant.Id, subject, tenant.Clients[0], otherTenant.Clients[0]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("client_not_in_tenant", body.GetProperty("error").GetString());
        Assert.Equal([otherTenant.Clients[0]], ClientIdsOf(body));
        await using var scope = this.Database(out var database);
        Assert.False(await database.TenantMemberships.AnyAsync(membership => membership.UserId == userId));
        Assert.False(await database.UserClientRoles.AnyAsync(role => role.UserId == userId));
    }

    [Fact]
    public async Task PutMembership_ForASubjectWithoutUser_IsNotFound()
    {
        this.Reset();
        using var platform = await this.PlatformAdministratorAsync();
        var tenant = await this.TenantWithClientsAsync(1);

        var response = await PutMembershipAsync(platform, tenant.Id, NewSubject(), tenant.Clients[0]);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("provisioned_user_not_found", (await ReadJsonAsync(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task PutMembership_InTheSystemTenant_IsRefused()
    {
        this.Reset();
        using var platform = await this.PlatformAdministratorAsync();
        var subject = NewSubject();
        await this.ProvisionAsync(platform, subject);

        var response = await PutMembershipAsync(platform, TenantCatalog.SystemTenantId, subject);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("system_tenant", (await ReadJsonAsync(response)).GetProperty("error").GetString());
    }

    /// <summary>
    ///     A person who left one organization keeps the other. The personal access token of the person carries the roles
    ///     of the account, so it reaches the clients of the remaining tenant and no client of the tenant the person left.
    /// </summary>
    [Fact]
    public async Task DeleteMembership_OfOneOfTwoMemberships_KeepsTheUserActiveAndTakesTheClientsOfThatTenantFromItsToken()
    {
        this.Reset();
        using var platform = await this.PlatformAdministratorAsync();
        var left = await this.TenantWithClientsAsync(1);
        var kept = await this.TenantWithClientsAsync(1);
        var subject = NewSubject();
        var userId = await this.ProvisionAsync(platform, subject);
        await PutMembershipAsync(platform, left.Id, subject, left.Clients[0]);
        await PutMembershipAsync(platform, kept.Id, subject, kept.Clients[0]);
        var personalAccessToken = await this.CreatePersonalAccessTokenAsync(userId);

        var response = await DeleteMembershipAsync(platform, left.Id, subject);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var extension = factory.CreateClientWithPersonalAccessToken(personalAccessToken);
        var me = await extension.GetAsync("/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var session = await ReadJsonAsync(me);
        Assert.Equal([kept.Id.ToString()], RoleKeys(session, "tenantRoles"));
        Assert.Equal([kept.Clients[0].ToString()], RoleKeys(session, "clientRoles"));
        var (account, _) = await this.ReadAsync(subject);
        Assert.True(account.IsActive);
    }

    [Fact]
    public async Task DeleteMembership_OfTheLastMembership_DisablesTheUserAndEndsItsSessionsAndPersonalAccessTokens()
    {
        this.Reset();
        using var platform = await this.PlatformAdministratorAsync();
        var tenant = await this.TenantWithClientsAsync(1);
        var subject = NewSubject();
        var userId = await this.ProvisionAsync(platform, subject);
        await PutMembershipAsync(platform, tenant.Id, subject, tenant.Clients[0]);
        var accessToken = factory.GenerateToken(userId, AppUserRole.User);
        var personalAccessToken = await this.CreatePersonalAccessTokenAsync(userId);
        var refreshToken = await this.SeedRefreshTokenAsync(userId);
        using var extension = factory.CreateClientWithPersonalAccessToken(personalAccessToken);
        Assert.Equal(HttpStatusCode.OK, (await extension.GetAsync("/auth/me")).StatusCode);

        var response = await DeleteMembershipAsync(platform, tenant.Id, subject);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await extension.GetAsync("/auth/me")).StatusCode);
        using var browser = factory.CreateClientWithAccessToken(accessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/auth/me")).StatusCode);
        var refresh = await factory.CreateClient().PostAsJsonAsync("/auth/refresh", new { refreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        var (account, provisioned) = await this.ReadAsync(subject);
        Assert.False(account.IsActive);
        Assert.NotNull(provisioned.InactiveWithoutMembershipSince);
        await using var scope = this.Database(out var database);
        Assert.All(await database.UserPats.AsNoTracking().Where(pat => pat.UserId == userId).ToListAsync(), pat => Assert.True(pat.IsRevoked));
        Assert.All(
            await database.RefreshTokens.AsNoTracking().Where(token => token.UserId == userId).ToListAsync(),
            token => Assert.NotNull(token.RevokedAt));
        factory.AuditLog.Received(1).ProvisionedUserDisabledWithoutMembership(Arg.Any<Guid>(), userId, account.Username);
    }

    [Fact]
    public async Task DeleteMembership_RepeatedOrForASubjectWithoutUser_AnswersNoContent()
    {
        this.Reset();
        using var platform = await this.PlatformAdministratorAsync();
        var tenant = await this.TenantWithClientsAsync(1);
        var subject = NewSubject();
        await this.ProvisionAsync(platform, subject);
        await PutMembershipAsync(platform, tenant.Id, subject, tenant.Clients[0]);

        var first = await DeleteMembershipAsync(platform, tenant.Id, subject);
        var repeated = await DeleteMembershipAsync(platform, tenant.Id, subject);
        var unknown = await DeleteMembershipAsync(platform, tenant.Id, NewSubject());

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, unknown.StatusCode);
    }

    /// <summary>
    ///     The first membership enables the account that the provisioning created inactive, and the membership after the
    ///     removal enables it again.
    /// </summary>
    [Fact]
    public async Task PutMembership_AfterTheLastMembershipWasRemoved_EnablesTheUserAgain()
    {
        this.Reset();
        using var platform = await this.PlatformAdministratorAsync();
        var tenant = await this.TenantWithClientsAsync(1);
        var subject = NewSubject();
        var userId = await this.ProvisionAsync(platform, subject);
        await PutMembershipAsync(platform, tenant.Id, subject, tenant.Clients[0]);
        await DeleteMembershipAsync(platform, tenant.Id, subject);

        var response = await PutMembershipAsync(platform, tenant.Id, subject, tenant.Clients[0]);

        Assert.True((await ReadJsonAsync(response)).GetProperty("isActive").GetBoolean());
        var (account, provisioned) = await this.ReadAsync(subject);
        Assert.True(account.IsActive);
        Assert.Null(provisioned.InactiveWithoutMembershipSince);
        factory.AuditLog.Received(2).ProvisionedUserEnabledByMembership(Arg.Any<Guid>(), userId, account.Username);
    }

    /// <summary>
    ///     A platform administrator of the installation disabled the account, for example after an abuse. The platform
    ///     sets the membership again when the workspaces of the organization change, and the account stays disabled.
    /// </summary>
    [Fact]
    public async Task PutMembership_DoesNotEnableAUserThatAPlatformAdministratorDisabled()
    {
        this.Reset();
        using var platform = await this.PlatformAdministratorAsync();
        var tenant = await this.TenantWithClientsAsync(1);
        var subject = NewSubject();
        var userId = await this.ProvisionAsync(platform, subject);
        await PutMembershipAsync(platform, tenant.Id, subject, tenant.Clients[0]);
        var disable = await platform.PatchAsJsonAsync($"/admin/users/{userId}", new { isActive = false });
        Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);

        var again = await PutMembershipAsync(platform, tenant.Id, subject, tenant.Clients[0]);
        await DeleteMembershipAsync(platform, tenant.Id, subject);
        var afterRemoval = await PutMembershipAsync(platform, tenant.Id, subject, tenant.Clients[0]);

        Assert.False((await ReadJsonAsync(again)).GetProperty("isActive").GetBoolean());
        Assert.False((await ReadJsonAsync(afterRemoval)).GetProperty("isActive").GetBoolean());
        var (account, _) = await this.ReadAsync(subject);
        Assert.False(account.IsActive);
    }

    /// <summary>
    ///     ProPR keeps a new account inactive until its first membership. A platform administrator enabled the account and
    ///     disabled it again before the platform set a membership, and that decision holds against the membership.
    /// </summary>
    [Fact]
    public async Task PutMembership_DoesNotEnableAUserThatAPlatformAdministratorDisabledBeforeItsFirstMembership()
    {
        this.Reset();
        using var platform = await this.PlatformAdministratorAsync();
        var tenant = await this.TenantWithClientsAsync(1);
        var subject = NewSubject();
        var userId = await this.ProvisionAsync(platform, subject);
        Assert.Equal(HttpStatusCode.NoContent, (await platform.PatchAsJsonAsync($"/admin/users/{userId}", new { isActive = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await platform.PatchAsJsonAsync($"/admin/users/{userId}", new { isActive = false })).StatusCode);

        var response = await PutMembershipAsync(platform, tenant.Id, subject, tenant.Clients[0]);

        Assert.False((await ReadJsonAsync(response)).GetProperty("isActive").GetBoolean());
        var (account, _) = await this.ReadAsync(subject);
        Assert.False(account.IsActive);
    }

    [Fact]
    public async Task ListMembers_ReturnsTheProvisionedUsersWithAMembershipInTheTenantAndTheirClients()
    {
        this.Reset();
        using var platform = await this.PlatformAdministratorAsync();
        var tenant = await this.TenantWithClientsAsync(2);
        var otherTenant = await this.TenantWithClientsAsync(1);
        var withClients = "a-" + NewSubject();
        var withoutClients = "b-" + NewSubject();
        var elsewhere = "c-" + NewSubject();
        await this.ProvisionAsync(platform, withClients);
        await this.ProvisionAsync(platform, withoutClients);
        await this.ProvisionAsync(platform, elsewhere);
        await PutMembershipAsync(platform, tenant.Id, withClients, tenant.Clients[1], tenant.Clients[0]);
        await PutMembershipAsync(platform, tenant.Id, withoutClients);
        await PutMembershipAsync(platform, otherTenant.Id, elsewhere, otherTenant.Clients[0]);
        var localUserId = await factory.SeedUserAsync($"local-{Guid.NewGuid():N}", $"local-{Guid.NewGuid():N}@customer.test");
        await factory.SeedTenantMembershipAsync(tenant.Id, localUserId, TenantRole.TenantUser);

        var response = await platform.GetAsync($"/admin/tenants/{tenant.Id}/provisioned-members");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var members = (await ReadJsonAsync(response)).EnumerateArray().ToList();
        Assert.Equal([withClients, withoutClients], members.Select(member => member.GetProperty("subject").GetString()));
        Assert.Equal(Sorted(tenant.Clients[0], tenant.Clients[1]), ClientIdsOf(members[0]));
        Assert.Empty(ClientIdsOf(members[1]));
        Assert.All(members, member => Assert.Equal("tenantUser", member.GetProperty("role").GetString()));
        Assert.All(members, member => Assert.True(member.GetProperty("isActive").GetBoolean()));
        Assert.Equal($"{withClients}@customer.test", members[0].GetProperty("email").GetString());
    }

    [Theory]
    [MemberData(nameof(ProvisioningRequests))]
    public async Task TheProvisioningEndpoints_RefuseCallersThatAreNotPlatformAdministrators(string method, string pathTemplate)
    {
        this.Reset();
        using var platform = await this.PlatformAdministratorAsync();
        var tenant = await this.TenantWithClientsAsync(1);
        var subject = NewSubject();
        var userId = await this.ProvisionAsync(platform, subject);
        await PutMembershipAsync(platform, tenant.Id, subject, tenant.Clients[0]);
        var tenantAdministratorId = await factory.SeedUserAsync($"admin-{Guid.NewGuid():N}", $"admin-{Guid.NewGuid():N}@customer.test");
        await factory.SeedTenantMembershipAsync(tenant.Id, tenantAdministratorId, TenantRole.TenantAdministrator);
        var newSubject = NewSubject();
        var path = pathTemplate
            .Replace("{tenantId}", tenant.Id.ToString(), StringComparison.Ordinal)
            .Replace("{subject}", Uri.EscapeDataString(subject), StringComparison.Ordinal)
            .Replace("{newSubject}", Uri.EscapeDataString(newSubject), StringComparison.Ordinal);
        var callers = new (string? AccessToken, HttpStatusCode Expected)[]
        {
            (null, HttpStatusCode.Unauthorized),
            (factory.GenerateToken(tenantAdministratorId, AppUserRole.User), HttpStatusCode.Forbidden),
            (factory.GenerateToken(userId, AppUserRole.User), HttpStatusCode.Forbidden),
        };

        foreach (var (accessToken, expected) in callers)
        {
            using var client = accessToken is null ? factory.CreateClient() : factory.CreateClientWithAccessToken(accessToken);
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (method == "PUT")
            {
                request.Content = JsonContent.Create(
                    pathTemplate.Contains("provisioned-users", StringComparison.Ordinal)
                        ? new { email = $"{newSubject}@customer.test" }
                        : (object)new { clientIds = Array.Empty<Guid>() });
            }

            var response = await client.SendAsync(request);

            Assert.Equal(expected, response.StatusCode);
        }

        Assert.Equal(0, await this.CountProvisionedAsync(newSubject));
        await using var scope = this.Database(out var database);
        Assert.Equal(
            [tenant.Clients[0]],
            await database.UserClientRoles.AsNoTracking().Where(role => role.UserId == userId).Select(role => role.ClientId).ToListAsync());
        Assert.True(await database.TenantMemberships.AnyAsync(membership => membership.UserId == userId && membership.TenantId == tenant.Id));
    }

    /// <summary>
    ///     The provisioning creates accounts without platform administrator rights and never gives them any. An account
    ///     with the rights that a provisioning record names stays as it is, and its membership is not touched.
    /// </summary>
    [Fact]
    public async Task TheProvisioningEndpoints_DoNotChangeAnAccountWithPlatformAdministratorRights()
    {
        this.Reset();
        using var platform = await this.PlatformAdministratorAsync();
        var tenant = await this.TenantWithClientsAsync(1);
        var subject = NewSubject();
        var administratorId = await factory.SeedUserAsync($"operator-{Guid.NewGuid():N}", $"{subject}@meister-dev.test", AppUserRole.Admin);
        await this.SeedProvisioningRecordAsync(administratorId, subject);
        await factory.SeedTenantMembershipAsync(tenant.Id, administratorId, TenantRole.TenantUser);

        var putUser = await PutUserAsync(platform, subject, $"{subject}@customer.test", null);
        var putMembership = await PutMembershipAsync(platform, tenant.Id, subject, tenant.Clients[0]);
        var deleteMembership = await DeleteMembershipAsync(platform, tenant.Id, subject);

        Assert.Equal(HttpStatusCode.Conflict, putUser.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, putMembership.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, deleteMembership.StatusCode);
        Assert.Equal("platform_administrator", (await ReadJsonAsync(putUser)).GetProperty("error").GetString());
        await using var scope = this.Database(out var database);
        var administrator = await database.AppUsers.AsNoTracking().SingleAsync(user => user.Id == administratorId);
        Assert.Equal(AppUserRole.Admin, administrator.GlobalRole);
        Assert.True(administrator.IsActive);
        Assert.True(await database.TenantMemberships.AnyAsync(membership => membership.UserId == administratorId));
        Assert.False(await database.UserClientRoles.AnyAsync(role => role.UserId == administratorId));
    }

    private static Task<HttpResponseMessage> PutUserAsync(HttpClient client, string subject, string email, string? displayName)
    {
        return client.PutAsJsonAsync($"/admin/provisioned-users/{Uri.EscapeDataString(subject)}", new { email, displayName });
    }

    private static Task<HttpResponseMessage> PutMembershipAsync(HttpClient client, Guid tenantId, string subject, params Guid[] clientIds)
    {
        return client.PutAsJsonAsync(
            $"/admin/tenants/{tenantId}/provisioned-members/{Uri.EscapeDataString(subject)}",
            new { clientIds });
    }

    private static Task<HttpResponseMessage> DeleteMembershipAsync(HttpClient client, Guid tenantId, string subject)
    {
        return client.DeleteAsync($"/admin/tenants/{tenantId}/provisioned-members/{Uri.EscapeDataString(subject)}");
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private static List<Guid> ClientIdsOf(JsonElement element)
    {
        return element.GetProperty("clientIds").EnumerateArray().Select(id => id.GetGuid()).ToList();
    }

    private static List<string> RoleKeys(JsonElement session, string property)
    {
        return session.GetProperty(property).EnumerateObject().Select(role => role.Name).Order(StringComparer.Ordinal).ToList();
    }

    private static List<Guid> Sorted(params Guid[] ids)
    {
        return ids.Order().ToList();
    }

    private static string NewSubject()
    {
        return $"customer-{Guid.NewGuid():N}";
    }

    private void Reset()
    {
        factory.ResetLicensing();
        factory.AuditLog.ClearReceivedCalls();
    }

    private async Task<HttpClient> PlatformAdministratorAsync()
    {
        return (await this.PlatformAdministratorWithIdAsync()).Client;
    }

    private async Task<(HttpClient Client, Guid UserId)> PlatformAdministratorWithIdAsync()
    {
        var username = $"platform-{Guid.NewGuid():N}";
        var userId = await factory.SeedUserAsync(username, $"{username}@meister-dev.test", AppUserRole.Admin);
        return (factory.CreateClientWithAccessToken(factory.GenerateToken(userId, AppUserRole.Admin)), userId);
    }

    private async Task<Guid> ProvisionAsync(HttpClient platform, string subject)
    {
        var response = await PutUserAsync(platform, subject, $"{subject}@customer.test", null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadJsonAsync(response)).GetProperty("userId").GetGuid();
    }

    private async Task<TestTenant> TenantWithClientsAsync(int clientCount)
    {
        var tenantId = await factory.SeedTenantAsync($"customer-{Guid.NewGuid():N}", "Customer organization");
        var clients = new List<Guid>();
        for (var index = 0; index < clientCount; index++)
        {
            clients.Add(await factory.SeedClientAsync(tenantId, $"Workspace {index}"));
        }

        return new TestTenant(tenantId, clients);
    }

    private async Task<string> CreatePersonalAccessTokenAsync(Guid userId)
    {
        using var person = factory.CreateClientWithAccessToken(factory.GenerateToken(userId, AppUserRole.User));
        var response = await person.PostAsJsonAsync("/users/me/pats", new { label = "browser extension" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadJsonAsync(response)).GetProperty("token").GetString()!;
    }

    private async Task<string> SeedRefreshTokenAsync(Guid userId)
    {
        var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var scope = factory.Services.CreateScope();
        var now = DateTimeOffset.UtcNow;
        await scope.ServiceProvider.GetRequiredService<IRefreshTokenRepository>().AddAsync(
            new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant(),
                ExpiresAt = now.AddHours(1),
                CreatedAt = now,
                LastUsedAt = now,
            });
        return rawToken;
    }

    private async Task SetClientRoleAsync(Guid userId, Guid clientId, ClientRole role)
    {
        await using var scope = this.Database(out var database);
        var assignment = await database.UserClientRoles.SingleAsync(r => r.UserId == userId && r.ClientId == clientId);
        assignment.Role = role;
        await database.SaveChangesAsync();
    }

    private async Task SeedProvisioningRecordAsync(Guid userId, string subject)
    {
        await using var scope = this.Database(out var database);
        var now = DateTimeOffset.UtcNow;
        database.ProvisionedUsers.Add(
            new ProvisionedUserRecord
            {
                UserId = userId,
                Issuer = ProvisionedUserApiFactory.Authority,
                Subject = subject,
                Email = $"{subject}@customer.test",
                CreatedAt = now,
                UpdatedAt = now,
            });
        await database.SaveChangesAsync();
    }

    private async Task<(AppUserRecord Account, ProvisionedUserRecord Provisioned)> ReadAsync(string subject)
    {
        await using var scope = this.Database(out var database);
        var provisioned = await database.ProvisionedUsers.AsNoTracking().SingleAsync(user => user.Subject == subject);
        var account = await database.AppUsers.AsNoTracking().SingleAsync(user => user.Id == provisioned.UserId);
        return (account, provisioned);
    }

    private async Task<int> CountProvisionedAsync(string subject)
    {
        await using var scope = this.Database(out var database);
        return await database.ProvisionedUsers.CountAsync(user => user.Subject == subject);
    }

    private AsyncServiceScope Database(out MeisterProPRDbContext database)
    {
        var scope = factory.Services.CreateAsyncScope();
        database = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        return scope;
    }

    private sealed record TestTenant(Guid Id, IReadOnlyList<Guid> Clients);
}

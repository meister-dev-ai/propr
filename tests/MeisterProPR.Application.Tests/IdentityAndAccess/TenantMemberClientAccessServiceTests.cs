// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterProPR.Infrastructure.Features.IdentityAndAccess.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Application.Tests.IdentityAndAccess;

public sealed class TenantMemberClientAccessServiceTests
{
    [Fact]
    public async Task AssignAsync_ClientInTenant_UpsertsAssignmentAndWritesAudit()
    {
        await using var db = CreateContext();
        var tenantId = Guid.NewGuid();
        var membershipId = SeedMember(db, tenantId, out var userId);
        var clientId = SeedClient(db, tenantId, "Acme Client");
        await db.SaveChangesAsync();
        var sut = new TenantMemberClientAccessService(db);

        var result = await sut.AssignAsync(tenantId, membershipId, clientId, ClientRole.ClientAdministrator);
        var assignments = await sut.ListMemberAccessAsync(tenantId, membershipId);

        Assert.Equal(TenantMemberClientAccessOutcome.Success, result.Outcome);
        Assert.Equal(ClientRole.ClientAdministrator, result.Assignment!.Role);
        Assert.NotNull(assignments);
        Assert.Single(assignments!);
        Assert.Equal(clientId, assignments![0].ClientId);
        Assert.Equal("Acme Client", assignments[0].ClientDisplayName);
        Assert.Contains(db.UserClientRoles, role => role.UserId == userId && role.ClientId == clientId);
        Assert.Contains(db.TenantAuditEntries, entry => entry.EventType == "tenant.client-access.assigned");
    }

    [Fact]
    public async Task AssignAsync_ExistingAssignment_UpdatesRoleInPlace()
    {
        await using var db = CreateContext();
        var tenantId = Guid.NewGuid();
        var membershipId = SeedMember(db, tenantId, out _);
        var clientId = SeedClient(db, tenantId, "Acme Client");
        await db.SaveChangesAsync();
        var sut = new TenantMemberClientAccessService(db);

        await sut.AssignAsync(tenantId, membershipId, clientId, ClientRole.ClientUser);
        await sut.AssignAsync(tenantId, membershipId, clientId, ClientRole.ClientAdministrator);
        var assignments = await sut.ListMemberAccessAsync(tenantId, membershipId);

        Assert.NotNull(assignments);
        Assert.Single(assignments!);
        Assert.Equal(ClientRole.ClientAdministrator, assignments![0].Role);
    }

    [Fact]
    public async Task AssignAsync_ClientOutsideTenant_ReturnsClientNotInTenant()
    {
        await using var db = CreateContext();
        var tenantId = Guid.NewGuid();
        var membershipId = SeedMember(db, tenantId, out _);
        var foreignClientId = SeedClient(db, Guid.NewGuid(), "Foreign Client");
        await db.SaveChangesAsync();
        var sut = new TenantMemberClientAccessService(db);

        var result = await sut.AssignAsync(tenantId, membershipId, foreignClientId, ClientRole.ClientUser);

        Assert.Equal(TenantMemberClientAccessOutcome.ClientNotInTenant, result.Outcome);
        Assert.Empty(db.UserClientRoles);
    }

    [Fact]
    public async Task AssignAsync_UnknownMembership_ReturnsMembershipNotFound()
    {
        await using var db = CreateContext();
        var tenantId = Guid.NewGuid();
        var clientId = SeedClient(db, tenantId, "Acme Client");
        await db.SaveChangesAsync();
        var sut = new TenantMemberClientAccessService(db);

        var result = await sut.AssignAsync(tenantId, Guid.NewGuid(), clientId, ClientRole.ClientUser);

        Assert.Equal(TenantMemberClientAccessOutcome.MembershipNotFound, result.Outcome);
    }

    [Fact]
    public async Task ListMemberAccessAsync_UnknownMembership_ReturnsNull()
    {
        await using var db = CreateContext();
        var sut = new TenantMemberClientAccessService(db);

        var assignments = await sut.ListMemberAccessAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.Null(assignments);
    }

    [Fact]
    public async Task RemoveAsync_ExistingAssignment_RevokesAndWritesAudit()
    {
        await using var db = CreateContext();
        var tenantId = Guid.NewGuid();
        var membershipId = SeedMember(db, tenantId, out _);
        var clientId = SeedClient(db, tenantId, "Acme Client");
        await db.SaveChangesAsync();
        var sut = new TenantMemberClientAccessService(db);
        await sut.AssignAsync(tenantId, membershipId, clientId, ClientRole.ClientUser);

        var outcome = await sut.RemoveAsync(tenantId, membershipId, clientId);
        var assignments = await sut.ListMemberAccessAsync(tenantId, membershipId);

        Assert.Equal(TenantMemberClientAccessOutcome.Success, outcome);
        Assert.Empty(assignments!);
        Assert.Contains(db.TenantAuditEntries, entry => entry.EventType == "tenant.client-access.removed");
    }

    [Fact]
    public async Task RemoveAsync_ClientInAnotherTenant_DoesNotRevokeCrossTenantAssignment()
    {
        await using var db = CreateContext();
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var membershipId = SeedMember(db, tenantId, out var userId);
        var foreignClientId = SeedClient(db, otherTenantId, "Foreign Client");
        db.UserClientRoles.Add(
            new UserClientRoleRecord
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ClientId = foreignClientId,
                Role = ClientRole.ClientUser,
                AssignedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();
        var sut = new TenantMemberClientAccessService(db);

        var outcome = await sut.RemoveAsync(tenantId, membershipId, foreignClientId);

        Assert.Equal(TenantMemberClientAccessOutcome.Success, outcome);
        Assert.Contains(db.UserClientRoles, role => role.ClientId == foreignClientId);
    }

    [Fact]
    public async Task ListTenantClientsAsync_ReturnsOnlyClientsInTenant()
    {
        await using var db = CreateContext();
        var tenantId = Guid.NewGuid();
        SeedClient(db, tenantId, "In Tenant");
        SeedClient(db, Guid.NewGuid(), "Other Tenant");
        await db.SaveChangesAsync();
        var sut = new TenantMemberClientAccessService(db);

        var clients = await sut.ListTenantClientsAsync(tenantId);

        Assert.Single(clients);
        Assert.Equal("In Tenant", clients[0].DisplayName);
    }

    [Fact]
    public async Task AssignAsync_SystemTenant_Throws()
    {
        await using var db = CreateContext();
        var membershipId = SeedMember(db, TenantCatalog.SystemTenantId, out _);
        var clientId = SeedClient(db, TenantCatalog.SystemTenantId, "System Client");
        await db.SaveChangesAsync();
        var sut = new TenantMemberClientAccessService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.AssignAsync(TenantCatalog.SystemTenantId, membershipId, clientId, ClientRole.ClientUser));
    }

    private static Guid SeedMember(MeisterProPRDbContext db, Guid tenantId, out Guid userId)
    {
        userId = Guid.NewGuid();
        db.AppUsers.Add(
            new AppUserRecord
            {
                Id = userId,
                Username = $"member-{userId:N}",
                Email = $"member-{userId:N}@acme.test",
                NormalizedEmail = $"MEMBER-{userId:N}@ACME.TEST",
                GlobalRole = AppUserRole.User,
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });

        var membershipId = Guid.NewGuid();
        db.TenantMemberships.Add(
            new TenantMembershipRecord
            {
                Id = membershipId,
                TenantId = tenantId,
                UserId = userId,
                Role = TenantRole.TenantUser,
                AssignedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

        return membershipId;
    }

    private static Guid SeedClient(MeisterProPRDbContext db, Guid tenantId, string displayName)
    {
        var clientId = Guid.NewGuid();
        db.Clients.Add(
            new ClientRecord
            {
                Id = clientId,
                TenantId = tenantId,
                DisplayName = displayName,
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });

        return clientId;
    }

    private static MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"TenantMemberClientAccessServiceTests_{Guid.NewGuid()}")
            .Options;

        return new MeisterProPRDbContext(options);
    }
}

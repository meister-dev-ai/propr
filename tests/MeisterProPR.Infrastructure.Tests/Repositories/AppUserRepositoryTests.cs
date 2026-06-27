// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Repositories;
using MeisterProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterProPR.Infrastructure.Tests.Repositories;

/// <summary>Integration tests for <see cref="AppUserRepository" /> hard-delete and admin-count queries.</summary>
[Collection("PostgresIntegration")]
public sealed class AppUserRepositoryTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private readonly List<Guid> _seededTenantIds = [];
    private readonly List<Guid> _seededUserIds = [];
    private DbContextOptions<MeisterProPRDbContext> _options = null!;

    public Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();
        this._options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (this._options is null)
        {
            return;
        }

        await using var db = new MeisterProPRDbContext(this._options);

        // Removing the tenants cascades their audit entries / memberships; remove any user that the
        // test under inspection did not already delete.
        await db.AppUsers.Where(u => this._seededUserIds.Contains(u.Id)).ExecuteDeleteAsync();
        await db.TenantAuditEntries.Where(e => this._seededTenantIds.Contains(e.TenantId)).ExecuteDeleteAsync();
        await db.Tenants.Where(t => this._seededTenantIds.Contains(t.Id)).ExecuteDeleteAsync();
    }

    private MeisterProPRDbContext NewContext()
    {
        return new MeisterProPRDbContext(this._options);
    }

    private static AppUserRecord NewUser(bool isActive, AppUserRole role)
    {
        return new AppUserRecord
        {
            Id = Guid.NewGuid(),
            Username = $"user-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            GlobalRole = role,
            IsActive = isActive,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    [Fact]
    public async Task DeleteAsync_RemovesUser_CascadesDependents_AndNullsAuditActor()
    {
        var tenantId = Guid.NewGuid();
        this._seededTenantIds.Add(tenantId);
        var user = NewUser(true, AppUserRole.User);
        this._seededUserIds.Add(user.Id);
        var auditId = Guid.NewGuid();

        await using (var seed = this.NewContext())
        {
            seed.Tenants.Add(
                new TenantRecord
                {
                    Id = tenantId,
                    Slug = $"t-{Guid.NewGuid():N}",
                    DisplayName = "Delete Test Tenant",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            seed.AppUsers.Add(user);
            seed.UserPats.Add(
                new UserPatRecord
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    TokenHash = "pat-hash",
                    Label = "ci",
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            seed.RefreshTokens.Add(
                new RefreshTokenRecord
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    TokenHash = "refresh-hash",
                    ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            seed.Set<TenantMembershipRecord>().Add(
                new TenantMembershipRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    UserId = user.Id,
                    Role = TenantRole.TenantUser,
                    AssignedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            seed.TenantAuditEntries.Add(
                new TenantAuditEntryRecord
                {
                    Id = auditId,
                    TenantId = tenantId,
                    ActorUserId = user.Id,
                    EventType = "test.event",
                    Summary = "seeded audit entry",
                    OccurredAt = DateTimeOffset.UtcNow,
                });
            await seed.SaveChangesAsync();
        }

        await using (var act = this.NewContext())
        {
            var repo = new AppUserRepository(act);
            await repo.DeleteAsync(user.Id);
        }

        await using var verify = this.NewContext();
        Assert.Null(await verify.AppUsers.FindAsync(user.Id));
        Assert.False(await verify.UserPats.AnyAsync(p => p.UserId == user.Id));
        Assert.False(await verify.RefreshTokens.AnyAsync(t => t.UserId == user.Id));
        Assert.False(await verify.Set<TenantMembershipRecord>().AnyAsync(m => m.UserId == user.Id));

        var audit = await verify.TenantAuditEntries.FirstOrDefaultAsync(e => e.Id == auditId);
        Assert.NotNull(audit);
        Assert.Null(audit!.ActorUserId);
    }

    [Fact]
    public async Task CountActiveAdminsAsync_CountsOnlyActiveAdmins()
    {
        await using var db = this.NewContext();
        var repo = new AppUserRepository(db);
        var baseline = await repo.CountActiveAdminsAsync();

        var activeAdmin1 = NewUser(true, AppUserRole.Admin);
        var activeAdmin2 = NewUser(true, AppUserRole.Admin);
        var disabledAdmin = NewUser(false, AppUserRole.Admin);
        var activeUser = NewUser(true, AppUserRole.User);
        foreach (var u in new[] { activeAdmin1, activeAdmin2, disabledAdmin, activeUser })
        {
            this._seededUserIds.Add(u.Id);
            db.AppUsers.Add(u);
        }

        await db.SaveChangesAsync();

        var count = await repo.CountActiveAdminsAsync();

        // Only the two active admins add to the count; the disabled admin and the active non-admin do not.
        Assert.Equal(baseline + 2, count);
    }
}

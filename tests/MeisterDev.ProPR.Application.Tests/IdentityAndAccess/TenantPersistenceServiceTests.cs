// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess.Persistence;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.Support;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.IdentityAndAccess;

public sealed class TenantPersistenceServiceTests
{
    [Fact]
    public async Task TenantAdminService_CreateAndPatchAsync_PersistsTenantState()
    {
        await using var db = CreateContext();
        var sut = new TenantAdminService(db);

        var created = await sut.CreateAsync("acme", "Acme Corp");
        var patched = await sut.PatchAsync(created.Id, "Acme Updated", localLoginEnabled: false);
        var bySlug = await sut.GetBySlugAsync("acme");

        Assert.NotNull(patched);
        Assert.Equal("Acme Updated", patched!.DisplayName);
        Assert.False(patched.LocalLoginEnabled);
        Assert.Equal(created.Id, bySlug!.Id);
        Assert.Contains(db.TenantAuditEntries, entry => entry.EventType == "tenant.created");
        Assert.Contains(db.TenantAuditEntries, entry => entry.EventType == "tenant.policy.updated");
    }

    [Fact]
    public async Task TenantMembershipService_UpsertAndListAsync_ReturnsTenantMembershipDtoWithUserContext()
    {
        await using var db = CreateContext();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        db.Tenants.Add(
            new TenantRecord
            {
                Id = tenantId,
                Slug = "acme",
                DisplayName = "Acme Corp",
                IsActive = true,
                LocalLoginEnabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        db.AppUsers.Add(
            new AppUserRecord
            {
                Id = userId,
                Username = "tenant.admin",
                Email = "tenant.admin@acme.test",
                NormalizedEmail = "TENANT.ADMIN@ACME.TEST",
                PasswordHash = null,
                GlobalRole = AppUserRole.User,
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();

        var sut = new TenantMembershipService(db);

        var membership = await sut.UpsertAsync(tenantId, userId, TenantRole.TenantAdministrator);
        var memberships = await sut.ListAsync(tenantId);

        Assert.Equal(TenantRole.TenantAdministrator, membership.Role);
        Assert.Single(memberships);
        Assert.Equal("tenant.admin", memberships[0].Username);
        Assert.Equal("tenant.admin@acme.test", memberships[0].Email);
        Assert.Contains(db.TenantAuditEntries, entry => entry.EventType == "tenant.membership.assigned");
    }

    [Fact]
    public async Task TenantSsoProviderService_CreateAndListEnabledForTenantSlugAsync_ReturnsProvider()
    {
        await using var db = CreateContext();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(
            new TenantRecord
            {
                Id = tenantId,
                Slug = "acme",
                DisplayName = "Acme Corp",
                IsActive = true,
                LocalLoginEnabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();

        var secretProtectionCodec = Substitute.For<ISecretProtectionCodec>();
        secretProtectionCodec.Protect(Arg.Any<string>(), Arg.Any<string>())
            .Returns(callInfo => $"protected::{callInfo.ArgAt<string>(0)}");
        var sut = new TenantSsoProviderService(db, secretProtectionCodec);

        var created = await sut.CreateAsync(
            tenantId,
            "Acme Entra",
            "entraId",
            "oidc",
            "https://login.microsoftonline.com/common/v2.0",
            "client-id",
            "client-secret",
            ["openid", "profile", "email"],
            ["acme.test"],
            true,
            true);
        var enabled = await sut.ListEnabledForTenantSlugAsync("acme");

        Assert.True(created.SecretConfigured);
        Assert.Single(enabled);
        Assert.Equal("Acme Entra", enabled[0].DisplayName);
    }

    [Fact]
    public async Task TenantAdminService_GetAllAsync_WithoutMultiTenancy_ReturnsOnlySystemTenant()
    {
        await using var db = CreateContext();
        await SeedSystemAndOneTenantAsync(db);
        var sut = new TenantAdminService(db, licensingCapabilityService: Licensing(multiTenancyAvailable: false));

        var tenants = await sut.GetAllAsync();

        Assert.Single(tenants);
        Assert.Equal(TenantCatalog.SystemTenantId, tenants[0].Id);
        Assert.False(tenants[0].IsEditable);
        Assert.False(tenants[0].LocalLoginEnabled);
    }

    [Fact]
    public async Task TenantAdminService_GetAllAsync_WithMultiTenancy_ReturnsEveryTenant()
    {
        await using var db = CreateContext();
        await SeedSystemAndOneTenantAsync(db);
        var sut = new TenantAdminService(db, licensingCapabilityService: Licensing(multiTenancyAvailable: true));

        var tenants = await sut.GetAllAsync();

        Assert.Equal(2, tenants.Count);
    }

    [Fact]
    public async Task TenantAdminService_GetByIdAsync_WithoutMultiTenancy_HidesANonSystemTenant()
    {
        await using var db = CreateContext();
        var tenantId = await SeedSystemAndOneTenantAsync(db);
        var sut = new TenantAdminService(db, licensingCapabilityService: Licensing(multiTenancyAvailable: false));

        Assert.Null(await sut.GetByIdAsync(tenantId));
        Assert.NotNull(await sut.GetByIdAsync(TenantCatalog.SystemTenantId));
    }

    // The slug lookup filters on the same terms as the lookup by id. It did not, so a tenant out of scope for the
    // installation was still reachable by name.
    [Fact]
    public async Task TenantAdminService_GetBySlugAsync_WithoutMultiTenancy_HidesANonSystemTenant()
    {
        await using var db = CreateContext();
        await SeedSystemAndOneTenantAsync(db);
        var sut = new TenantAdminService(db, licensingCapabilityService: Licensing(multiTenancyAvailable: false));

        Assert.Null(await sut.GetBySlugAsync("acme"));
        Assert.NotNull(await sut.GetBySlugAsync(TenantCatalog.SystemTenantSlug));
    }

    [Fact]
    public async Task TenantAdminService_GetBySlugAsync_WithMultiTenancy_ReturnsTheTenant()
    {
        await using var db = CreateContext();
        var tenantId = await SeedSystemAndOneTenantAsync(db);
        var sut = new TenantAdminService(db, licensingCapabilityService: Licensing(multiTenancyAvailable: true));

        var tenant = await sut.GetBySlugAsync("acme");

        Assert.Equal(tenantId, tenant?.Id);
    }

    [Fact]
    public async Task TenantAdminService_ExistsAsync_WithoutMultiTenancy_ReportsOnlyTheSystemTenant()
    {
        await using var db = CreateContext();
        var tenantId = await SeedSystemAndOneTenantAsync(db);
        var sut = new TenantAdminService(db, licensingCapabilityService: Licensing(multiTenancyAvailable: false));

        Assert.False(await sut.ExistsAsync(tenantId));
        Assert.True(await sut.ExistsAsync(TenantCatalog.SystemTenantId));
    }

    [Fact]
    public async Task TenantAdminService_CreateAsync_WithoutMultiTenancy_IsRefused()
    {
        await using var db = CreateContext();
        var sut = new TenantAdminService(db, licensingCapabilityService: Licensing(multiTenancyAvailable: false));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.CreateAsync("acme", "Acme Corp"));

        Assert.Equal(
            "A commercial license is required to use more than the built-in System tenant, including in self-hosted deployments.",
            ex.Message);
        Assert.Empty(db.Tenants);
    }

    // No licensing service means no installation state to read, which is what a deployment without a database
    // configured looks like. Tenancy stays unrestricted there rather than collapsing to the System tenant.
    [Fact]
    public async Task TenantAdminService_WithoutTheLicensingModule_LeavesTenancyUnrestricted()
    {
        await using var db = CreateContext();
        var tenantId = await SeedSystemAndOneTenantAsync(db);
        var sut = new TenantAdminService(db);

        Assert.Equal(2, (await sut.GetAllAsync()).Count);
        Assert.NotNull(await sut.GetByIdAsync(tenantId));
        Assert.NotNull(await sut.GetBySlugAsync("acme"));
        Assert.True(await sut.ExistsAsync(tenantId));
    }

    // Creation follows the same posture as the reads: without the licensing module there is nothing to refuse on.
    [Fact]
    public async Task TenantAdminService_CreateAsync_WithoutTheLicensingModule_IsPermitted()
    {
        await using var db = CreateContext();
        var sut = new TenantAdminService(db);

        var created = await sut.CreateAsync("acme", "Acme Corp");

        Assert.Equal("acme", created.Slug);
        Assert.Single(db.Tenants);
    }

    [Fact]
    public async Task TenantAdminService_PatchAsync_SystemTenant_ThrowsInvalidOperationException()
    {
        await using var db = CreateContext();
        db.Tenants.Add(
            new TenantRecord
            {
                Id = TenantCatalog.SystemTenantId,
                Slug = TenantCatalog.SystemTenantSlug,
                DisplayName = TenantCatalog.SystemTenantDisplayName,
                IsActive = true,
                LocalLoginEnabled = false,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();

        var sut = new TenantAdminService(db);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.PatchAsync(TenantCatalog.SystemTenantId, "Renamed System"));

        Assert.Equal("The internal System tenant cannot be modified.", ex.Message);
    }

    [Fact]
    public async Task TenantMembershipService_UpsertAsync_SystemTenant_ThrowsInvalidOperationException()
    {
        await using var db = CreateContext();
        var userId = Guid.NewGuid();

        db.Tenants.Add(
            new TenantRecord
            {
                Id = TenantCatalog.SystemTenantId,
                Slug = TenantCatalog.SystemTenantSlug,
                DisplayName = TenantCatalog.SystemTenantDisplayName,
                IsActive = true,
                LocalLoginEnabled = false,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        db.AppUsers.Add(
            new AppUserRecord
            {
                Id = userId,
                Username = "system.admin",
                Email = "system.admin@meister.test",
                NormalizedEmail = "SYSTEM.ADMIN@MEISTER.TEST",
                PasswordHash = null,
                GlobalRole = AppUserRole.User,
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();

        var sut = new TenantMembershipService(db);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.UpsertAsync(TenantCatalog.SystemTenantId, userId, TenantRole.TenantAdministrator));

        Assert.Equal("The internal System tenant cannot be modified.", ex.Message);
    }

    /// <summary>
    ///     A licensing service that answers only for the multi-tenancy capability, which is the gate here. The
    ///     snapshot comes from the real catalog, so the refusal message is the one an installation would show.
    /// </summary>
    private static ILicensingCapabilityService Licensing(bool multiTenancyAvailable)
    {
        var definition = new StaticPremiumCapabilityCatalog().Get(PremiumCapabilityKey.MultiTenancy)
                         ?? throw new InvalidOperationException("The catalog has no multi-tenancy capability.");
        var snapshot = new CapabilitySnapshot(
            definition.Key,
            definition.DisplayName,
            definition.RequiresCommercial,
            PremiumCapabilityOverrideState.Default,
            multiTenancyAvailable,
            multiTenancyAvailable ? null : definition.CommercialRequiredMessage,
            multiTenancyAvailable ? null : PremiumCapabilityUnavailableReason.NoLicense);

        var licensingCapabilityService = Substitute.For<ILicensingCapabilityService>();
        licensingCapabilityService
            .IsEnabledAsync(PremiumCapabilityKey.MultiTenancy, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<bool>(multiTenancyAvailable));
        licensingCapabilityService
            .GetCapabilityAsync(PremiumCapabilityKey.MultiTenancy, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(snapshot));

        return licensingCapabilityService;
    }

    /// <summary>Seeds the System tenant plus one ordinary tenant, and returns the ordinary tenant's id.</summary>
    private static async Task<Guid> SeedSystemAndOneTenantAsync(MeisterProPRDbContext db)
    {
        var tenantId = Guid.NewGuid();
        db.Tenants.AddRange(
            new TenantRecord
            {
                Id = TenantCatalog.SystemTenantId,
                Slug = TenantCatalog.SystemTenantSlug,
                DisplayName = TenantCatalog.SystemTenantDisplayName,
                IsActive = true,
                LocalLoginEnabled = false,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            new TenantRecord
            {
                Id = tenantId,
                Slug = "acme",
                DisplayName = "Acme Corp",
                IsActive = true,
                LocalLoginEnabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();

        return tenantId;
    }

    private static MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"TenantPersistenceServiceTests_{Guid.NewGuid()}")
            .Options;

        return new MeisterProPRDbContext(options);
    }
}

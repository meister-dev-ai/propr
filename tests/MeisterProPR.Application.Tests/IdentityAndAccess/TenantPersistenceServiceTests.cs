// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Licensing.Dtos;
using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Application.Features.Licensing.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterProPR.Infrastructure.Features.IdentityAndAccess.Persistence;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace MeisterProPR.Application.Tests.IdentityAndAccess;

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
    public async Task TenantAdminService_GetAllAsync_CommunityEdition_ReturnsOnlySystemTenant()
    {
        await using var db = CreateContext();
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
                Id = Guid.NewGuid(),
                Slug = "acme",
                DisplayName = "Acme Corp",
                IsActive = true,
                LocalLoginEnabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();

        var licensingCapabilityService = Substitute.For<ILicensingCapabilityService>();
        licensingCapabilityService.GetSummaryAsync(Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(
                    new LicensingSummaryDto(
                        InstallationEdition.Community,
                        null,
                        [])));
        var sut = new TenantAdminService(db, licensingCapabilityService: licensingCapabilityService);

        var tenants = await sut.GetAllAsync();

        Assert.Single(tenants);
        Assert.Equal(TenantCatalog.SystemTenantId, tenants[0].Id);
        Assert.False(tenants[0].IsEditable);
        Assert.False(tenants[0].LocalLoginEnabled);
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

    private static MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"TenantPersistenceServiceTests_{Guid.NewGuid()}")
            .Options;

        return new MeisterProPRDbContext(options);
    }
}

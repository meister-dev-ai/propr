// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.AI;
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
    private const string LoadedFamilyKey = "meisterdev/azureOpenAi";

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

    // The admin read is what an operator consults after a tenant starts refusing every provider, so it has to
    // report the entry that caused it and not quietly leave it out.
    [Fact]
    public async Task TenantAdminService_GetByIdAsync_ReportsAnAllowListEntryNoFamilyClaims()
    {
        await using var db = CreateContext();
        var sut = new TenantAdminService(db, providerDrivers: Families());
        var created = await sut.CreateAsync("acme", "Acme Corp");

        var tenant = await db.Tenants.FindAsync(created.Id);
        tenant!.AllowedAiProviderKinds = [LoadedFamilyKey, "Acme.Llm"];
        await db.SaveChangesAsync();

        var read = await sut.GetByIdAsync(created.Id);

        Assert.NotNull(read);
        Assert.Equal([LoadedFamilyKey], read!.AllowedAiProviderKinds);
        Assert.Equal(["Acme.Llm"], read.UnresolvedAiProviderKinds);
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

    // No licensing service means no installation state to read, which a deployment without a database
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

    // Saving a provider policy keeps an entry this build cannot name. The field is typed as the closed family
    // enum, so a caller cannot send one back, and dropping what it could not express would lift the restriction
    // that entry carries — turning a save of something else into a silent removal.
    [Fact]
    public async Task TenantAdminService_PatchAsync_KeepsAnAllowListEntryNoFamilyClaims()
    {
        await using var db = CreateContext();
        var sut = new TenantAdminService(db, providerDrivers: Families());
        var created = await sut.CreateAsync("acme", "Acme Corp");

        var tenant = await db.Tenants.FindAsync(created.Id);
        tenant!.AllowedAiProviderKinds = [LoadedFamilyKey, "Acme.Llm"];
        await db.SaveChangesAsync();

        await sut.PatchAsync(created.Id, allowedAiProviderKinds: [LoadedFamilyKey]);

        var read = await sut.GetByIdAsync(created.Id);

        Assert.NotNull(read);
        Assert.Equal([LoadedFamilyKey], read!.AllowedAiProviderKinds);
        Assert.Equal(["Acme.Llm"], read.UnresolvedAiProviderKinds);
    }

    // Keeping such an entry across every save would leave a tenant whose only entry stopped resolving refusing
    // every provider with no way back, so removing one is its own input. It names the entry, and that keeps
    // the removal deliberate.
    [Fact]
    public async Task TenantAdminService_PatchAsync_RemovesTheAllowListEntryItIsGiven()
    {
        await using var db = CreateContext();
        var sut = new TenantAdminService(db, providerDrivers: Families());
        var created = await sut.CreateAsync("acme", "Acme Corp");

        var tenant = await db.Tenants.FindAsync(created.Id);
        tenant!.AllowedAiProviderKinds = ["Acme.Llm", "Acme.Other"];
        await db.SaveChangesAsync();

        await sut.PatchAsync(created.Id, removedUnresolvedAiProviderKinds: ["Acme.Llm"]);

        var read = await sut.GetByIdAsync(created.Id);

        Assert.NotNull(read);
        Assert.Equal(["Acme.Other"], read!.UnresolvedAiProviderKinds);
        Assert.Empty(read.AllowedAiProviderKinds!);
    }

    // Removing the last one lifts the restriction, which is how a tenant whose policy this build can no longer
    // read gets back to a usable state without being able to name the entry as a family.
    [Fact]
    public async Task TenantAdminService_PatchAsync_RemovingTheLastUnclaimedEntryLiftsTheRestriction()
    {
        await using var db = CreateContext();
        var sut = new TenantAdminService(db, providerDrivers: Families());
        var created = await sut.CreateAsync("acme", "Acme Corp");

        var tenant = await db.Tenants.FindAsync(created.Id);
        tenant!.AllowedAiProviderKinds = ["Acme.Llm"];
        await db.SaveChangesAsync();

        await sut.PatchAsync(created.Id, removedUnresolvedAiProviderKinds: ["Acme.Llm"]);

        // Read through the translation the enforcement path uses, because "no longer restricted" is the property
        // that matters and the stored columns are what it is decided from.
        var policy = TenantProviderPolicy.FromStored(
            tenant.AllowedAiProviderKinds,
            tenant.AllowedAiEndpointHosts,
            Families());

        Assert.False(policy.IsRestricted);
        Assert.True(policy.IsAllowed(LoadedFamilyKey));
    }

    // The families are not restated when an entry is removed, so they are carried rather than cleared: a removal
    // that also lifted the family restriction would be the silent side effect this input exists to avoid.
    [Fact]
    public async Task TenantAdminService_PatchAsync_RemovingAnEntryLeavesThePermittedFamiliesAlone()
    {
        await using var db = CreateContext();
        var sut = new TenantAdminService(db, providerDrivers: Families());
        var created = await sut.CreateAsync("acme", "Acme Corp");

        var tenant = await db.Tenants.FindAsync(created.Id);
        tenant!.AllowedAiProviderKinds = [LoadedFamilyKey, "Acme.Llm"];
        await db.SaveChangesAsync();

        await sut.PatchAsync(created.Id, removedUnresolvedAiProviderKinds: ["Acme.Llm"]);

        var read = await sut.GetByIdAsync(created.Id);

        Assert.NotNull(read);
        Assert.Equal([LoadedFamilyKey], read!.AllowedAiProviderKinds);
        Assert.Empty(read.UnresolvedAiProviderKinds!);
    }

    // One loaded family, so an allow-list entry has something to resolve against and everything else is an
    // entry no family claims.
    private static IAiProviderDriverRegistry Families()
    {
        var declaration = new ProviderDeclaration
        {
            Key = LoadedFamilyKey,
            Label = "Loaded family",
            Version = "1.0",
            ContractVersion = ProviderContract.Version,
            AuthModes = [new ProviderDeclaredAuthMode(LoadedFamilyKey + ":ApiKey", [AiCredentialFieldSupport.ApiKey])],
            ProtocolModes = new ProviderDeclaredProtocolModes([ProviderDeclaredProtocolModes.Auto]),
            ConformanceInputs = new ProviderConformanceInputs(LoadedFamilyKey + ":ApiKey"),
        };

        var driver = Substitute.For<IAiProviderDriver>();
        driver.Declaration.Returns(declaration);
        driver.SupportedAuthModes.Returns(declaration.SupportedAuthModes);
        driver.SupportedProtocolModes.Returns(declaration.ProtocolModes.Supported);
        driver.CredentialFields.Returns(declaration.CredentialFields);

        return new AiProviderRegistry([driver]);
    }
}

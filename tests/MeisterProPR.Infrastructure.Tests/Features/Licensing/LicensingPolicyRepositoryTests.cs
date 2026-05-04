// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Features.Licensing.Persistence;
using MeisterProPR.Infrastructure.Features.Licensing.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MeisterProPR.Infrastructure.Tests.Features.Licensing;

public sealed class LicensingPolicyRepositoryTests
{
    [Fact]
    public async Task GetAsync_EmptyStore_SeedsCommunityDefaults()
    {
        await using var db = CreateContext();
        var sut = new LicensingPolicyRepository(db, new StaticPremiumCapabilityCatalog());

        var policy = await sut.GetAsync(CancellationToken.None);

        Assert.Equal(InstallationEdition.Community, policy.Edition);
        Assert.Null(policy.ActivatedAt);
        Assert.Null(policy.ActivatedByUserId);
        Assert.Empty(policy.CapabilityOverrides);
        Assert.Equal(1, await db.InstallationEditions.CountAsync());
    }

    [Fact]
    public async Task UpdateAsync_CommercialEdition_PersistsActivationAndOverrideAuditFields()
    {
        await using var db = CreateContext();
        var actorUserId = Guid.NewGuid();
        var sut = new LicensingPolicyRepository(db, new StaticPremiumCapabilityCatalog());

        var policy = await sut.UpdateAsync(
            InstallationEdition.Commercial,
            [
                new CapabilityOverrideMutation(
                    PremiumCapabilityKey.MultipleScmProviders,
                    PremiumCapabilityOverrideState.Disabled),
            ],
            actorUserId,
            CancellationToken.None);

        Assert.Equal(InstallationEdition.Commercial, policy.Edition);
        Assert.NotNull(policy.ActivatedAt);
        Assert.Equal(actorUserId, policy.ActivatedByUserId);
        Assert.Equal(actorUserId, policy.UpdatedByUserId);
        Assert.Equal(PremiumCapabilityOverrideState.Disabled, policy.GetOverrideState(PremiumCapabilityKey.MultipleScmProviders));

        var overrideRecord = await db.PremiumCapabilityOverrides.SingleAsync();
        Assert.Equal(actorUserId, overrideRecord.UpdatedByUserId);
        Assert.Equal(PremiumCapabilityOverrideState.Disabled, overrideRecord.OverrideState);
    }

    [Fact]
    public async Task UpdateAsync_DowngradeToCommunity_ClearsActivationMetadataAndDefaultOverrideRows()
    {
        await using var db = CreateContext();
        var sut = new LicensingPolicyRepository(db, new StaticPremiumCapabilityCatalog());

        await sut.UpdateAsync(
            InstallationEdition.Commercial,
            [
                new CapabilityOverrideMutation(
                    PremiumCapabilityKey.SsoAuthentication,
                    PremiumCapabilityOverrideState.Disabled),
            ],
            Guid.NewGuid(),
            CancellationToken.None);

        var downgradedPolicy = await sut.UpdateAsync(
            InstallationEdition.Community,
            [
                new CapabilityOverrideMutation(
                    PremiumCapabilityKey.SsoAuthentication,
                    PremiumCapabilityOverrideState.Default),
            ],
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.Equal(InstallationEdition.Community, downgradedPolicy.Edition);
        Assert.Null(downgradedPolicy.ActivatedAt);
        Assert.Null(downgradedPolicy.ActivatedByUserId);
        Assert.Empty(downgradedPolicy.CapabilityOverrides);
        Assert.Empty(await db.PremiumCapabilityOverrides.ToListAsync());
    }

    private static MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"TestDb_LicensingPolicy_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new MeisterProPRDbContext(options);
    }
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Features.Licensing.Persistence;
using MeisterProPR.Infrastructure.Features.Licensing.Support;
using MeisterProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using FactAttribute = Xunit.SkippableFactAttribute;

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

[Collection("PostgresIntegration")]
public sealed class LicensingPolicyRepositoryPostgresTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;

    public LicensingPolicyRepositoryPostgresTests(PostgresContainerFixture fixture)
    {
        this._fixture = fixture;
    }

    public Task InitializeAsync()
    {
        this._fixture.SkipIfUnavailable();
        return this.ResetTablesAsync();
    }

    public Task DisposeAsync()
    {
        return this.ResetTablesAsync();
    }

    [Fact]
    public async Task GetAsync_ConcurrentFirstRead_SeedsSingletonOnlyOnce()
    {
        await using var db1 = this.CreatePostgresContext();
        await using var db2 = this.CreatePostgresContext();
        var sut1 = new LicensingPolicyRepository(db1, new StaticPremiumCapabilityCatalog());
        var sut2 = new LicensingPolicyRepository(db2, new StaticPremiumCapabilityCatalog());

        var policies = await Task.WhenAll(
            sut1.GetAsync(CancellationToken.None),
            sut2.GetAsync(CancellationToken.None));

        Assert.All(policies, policy => Assert.Equal(InstallationEdition.Community, policy.Edition));

        await using var verificationDb = this.CreatePostgresContext();
        Assert.Equal(1, await verificationDb.InstallationEditions.CountAsync());
        var record = await verificationDb.InstallationEditions.SingleAsync();
        Assert.Equal(1, record.Id);
        Assert.Equal(InstallationEdition.Community, record.Edition);
    }

    private async Task ResetTablesAsync()
    {
        await using var db = this.CreatePostgresContext();
        await db.PremiumCapabilityOverrides.ExecuteDeleteAsync();
        await db.InstallationEditions.ExecuteDeleteAsync();
    }

    private MeisterProPRDbContext CreatePostgresContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(this._fixture.ConnectionString, o => o.UseVector())
            .Options;

        return new MeisterProPRDbContext(options);
    }
}

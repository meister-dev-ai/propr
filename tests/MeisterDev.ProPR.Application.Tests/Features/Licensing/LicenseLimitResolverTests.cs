// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Licensing.Services;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.Persistence;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.Support;
using MeisterDev.ProPR.Licensing;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.Licensing;

/// <summary>
///     Limit resolution over license states built by signing documents with a chain generated for the test and
///     verifying them with the product's own verifier. The writer and the reader therefore leave every stated
///     limit either unlimited or a non-negative count by the time the resolver reads it.
/// </summary>
public sealed class LicenseLimitResolverTests : IDisposable
{
    private const long LicensedNumber = 7;

    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly LicenseTrustAnchor _anchor;
    private readonly LicenseTestChain _chain;
    private readonly LicenseVerifier _verifier;

    public LicenseLimitResolverTests()
    {
        this._chain = LicenseTestChain.Create();
        this._anchor = this._chain.CreateAnchor();
        this._verifier = new LicenseVerifier(this._anchor);
    }

    /// <summary>What the license on file states for the quota under test.</summary>
    public enum Fixture
    {
        /// <summary>No license is on file.</summary>
        None = 0,

        /// <summary>A verified license that states nothing for the quota.</summary>
        Absent = 1,

        /// <summary>A verified license that states a count for the quota.</summary>
        Number = 2,

        /// <summary>A verified license that states the quota as unlimited.</summary>
        Unlimited = 3,
    }

    public void Dispose()
    {
        this._anchor.Dispose();
        this._chain.Dispose();
    }

    // Each quota gets its own matrix over every license fixture and every stage. The licensed value applies
    // through the active, warning and grace stages, and the community value applies before the term starts,
    // once the grace window has closed, for a quota the license leaves out, and for an installation with no
    // license, whose stage axis collapses because it has no term.
    //
    // The parallel-execution capability is available throughout, which is why the licensed concurrent-review
    // number applies in these rows. What happens without it has its own tests below.
    //
    // Clients: the community value is unlimited as well, so on the unlimited rows only the source distinguishes
    // a licensed answer from a community one.
    [Theory]
    [InlineData(Fixture.None, LicenseStage.None, LicenseLimitCeiling.Unlimited, null, LicenseLimitSource.Community)]
    [InlineData(Fixture.Absent, LicenseStage.NotYetValid, LicenseLimitCeiling.Unlimited, null, LicenseLimitSource.Community)]
    [InlineData(Fixture.Absent, LicenseStage.Active, LicenseLimitCeiling.Unlimited, null, LicenseLimitSource.Community)]
    [InlineData(Fixture.Absent, LicenseStage.Warning, LicenseLimitCeiling.Unlimited, null, LicenseLimitSource.Community)]
    [InlineData(Fixture.Absent, LicenseStage.Grace, LicenseLimitCeiling.Unlimited, null, LicenseLimitSource.Community)]
    [InlineData(Fixture.Absent, LicenseStage.Reverted, LicenseLimitCeiling.Unlimited, null, LicenseLimitSource.Community)]
    [InlineData(Fixture.Number, LicenseStage.NotYetValid, LicenseLimitCeiling.Unlimited, null, LicenseLimitSource.Community)]
    [InlineData(Fixture.Number, LicenseStage.Active, LicenseLimitCeiling.Count, 7, LicenseLimitSource.License)]
    [InlineData(Fixture.Number, LicenseStage.Warning, LicenseLimitCeiling.Count, 7, LicenseLimitSource.License)]
    [InlineData(Fixture.Number, LicenseStage.Grace, LicenseLimitCeiling.Count, 7, LicenseLimitSource.License)]
    [InlineData(Fixture.Number, LicenseStage.Reverted, LicenseLimitCeiling.Unlimited, null, LicenseLimitSource.Community)]
    [InlineData(Fixture.Unlimited, LicenseStage.NotYetValid, LicenseLimitCeiling.Unlimited, null, LicenseLimitSource.Community)]
    [InlineData(Fixture.Unlimited, LicenseStage.Active, LicenseLimitCeiling.Unlimited, null, LicenseLimitSource.License)]
    [InlineData(Fixture.Unlimited, LicenseStage.Warning, LicenseLimitCeiling.Unlimited, null, LicenseLimitSource.License)]
    [InlineData(Fixture.Unlimited, LicenseStage.Grace, LicenseLimitCeiling.Unlimited, null, LicenseLimitSource.License)]
    [InlineData(Fixture.Unlimited, LicenseStage.Reverted, LicenseLimitCeiling.Unlimited, null, LicenseLimitSource.Community)]
    public Task ResolveAsync_TheClientsQuota_ResolvesOverTheFullMatrix(
        Fixture fixture,
        LicenseStage stage,
        LicenseLimitCeiling expectedCeiling,
        int? expectedCount,
        LicenseLimitSource expectedSource) =>
        this.AssertResolutionAsync(
            LicenseLimitKey.Clients,
            fixture,
            stage,
            expectedCeiling,
            expectedCount,
            expectedSource);

    // Runners: the community value is zero, and a license past its grace window resolves to the same number.
    [Theory]
    [InlineData(Fixture.None, LicenseStage.None, LicenseLimitCeiling.Count, 0, LicenseLimitSource.Community)]
    [InlineData(Fixture.Absent, LicenseStage.NotYetValid, LicenseLimitCeiling.Count, 0, LicenseLimitSource.Community)]
    [InlineData(Fixture.Absent, LicenseStage.Active, LicenseLimitCeiling.Count, 0, LicenseLimitSource.Community)]
    [InlineData(Fixture.Absent, LicenseStage.Warning, LicenseLimitCeiling.Count, 0, LicenseLimitSource.Community)]
    [InlineData(Fixture.Absent, LicenseStage.Grace, LicenseLimitCeiling.Count, 0, LicenseLimitSource.Community)]
    [InlineData(Fixture.Absent, LicenseStage.Reverted, LicenseLimitCeiling.Count, 0, LicenseLimitSource.Community)]
    [InlineData(Fixture.Number, LicenseStage.NotYetValid, LicenseLimitCeiling.Count, 0, LicenseLimitSource.Community)]
    [InlineData(Fixture.Number, LicenseStage.Active, LicenseLimitCeiling.Count, 7, LicenseLimitSource.License)]
    [InlineData(Fixture.Number, LicenseStage.Warning, LicenseLimitCeiling.Count, 7, LicenseLimitSource.License)]
    [InlineData(Fixture.Number, LicenseStage.Grace, LicenseLimitCeiling.Count, 7, LicenseLimitSource.License)]
    [InlineData(Fixture.Number, LicenseStage.Reverted, LicenseLimitCeiling.Count, 0, LicenseLimitSource.Community)]
    [InlineData(Fixture.Unlimited, LicenseStage.NotYetValid, LicenseLimitCeiling.Count, 0, LicenseLimitSource.Community)]
    [InlineData(Fixture.Unlimited, LicenseStage.Active, LicenseLimitCeiling.Unlimited, null, LicenseLimitSource.License)]
    [InlineData(Fixture.Unlimited, LicenseStage.Warning, LicenseLimitCeiling.Unlimited, null, LicenseLimitSource.License)]
    [InlineData(Fixture.Unlimited, LicenseStage.Grace, LicenseLimitCeiling.Unlimited, null, LicenseLimitSource.License)]
    [InlineData(Fixture.Unlimited, LicenseStage.Reverted, LicenseLimitCeiling.Count, 0, LicenseLimitSource.Community)]
    public Task ResolveAsync_TheRunnersQuota_ResolvesOverTheFullMatrix(
        Fixture fixture,
        LicenseStage stage,
        LicenseLimitCeiling expectedCeiling,
        int? expectedCount,
        LicenseLimitSource expectedSource) =>
        this.AssertResolutionAsync(
            LicenseLimitKey.Runners,
            fixture,
            stage,
            expectedCeiling,
            expectedCount,
            expectedSource);

    // Concurrent reviews: the community value is one review at a time.
    [Theory]
    [InlineData(Fixture.None, LicenseStage.None, LicenseLimitCeiling.Count, 1, LicenseLimitSource.Community)]
    [InlineData(Fixture.Absent, LicenseStage.NotYetValid, LicenseLimitCeiling.Count, 1, LicenseLimitSource.Community)]
    [InlineData(Fixture.Absent, LicenseStage.Active, LicenseLimitCeiling.Count, 1, LicenseLimitSource.Community)]
    [InlineData(Fixture.Absent, LicenseStage.Warning, LicenseLimitCeiling.Count, 1, LicenseLimitSource.Community)]
    [InlineData(Fixture.Absent, LicenseStage.Grace, LicenseLimitCeiling.Count, 1, LicenseLimitSource.Community)]
    [InlineData(Fixture.Absent, LicenseStage.Reverted, LicenseLimitCeiling.Count, 1, LicenseLimitSource.Community)]
    [InlineData(Fixture.Number, LicenseStage.NotYetValid, LicenseLimitCeiling.Count, 1, LicenseLimitSource.Community)]
    [InlineData(Fixture.Number, LicenseStage.Active, LicenseLimitCeiling.Count, 7, LicenseLimitSource.License)]
    [InlineData(Fixture.Number, LicenseStage.Warning, LicenseLimitCeiling.Count, 7, LicenseLimitSource.License)]
    [InlineData(Fixture.Number, LicenseStage.Grace, LicenseLimitCeiling.Count, 7, LicenseLimitSource.License)]
    [InlineData(Fixture.Number, LicenseStage.Reverted, LicenseLimitCeiling.Count, 1, LicenseLimitSource.Community)]
    [InlineData(Fixture.Unlimited, LicenseStage.NotYetValid, LicenseLimitCeiling.Count, 1, LicenseLimitSource.Community)]
    [InlineData(Fixture.Unlimited, LicenseStage.Active, LicenseLimitCeiling.Unlimited, null, LicenseLimitSource.License)]
    [InlineData(Fixture.Unlimited, LicenseStage.Warning, LicenseLimitCeiling.Unlimited, null, LicenseLimitSource.License)]
    [InlineData(Fixture.Unlimited, LicenseStage.Grace, LicenseLimitCeiling.Unlimited, null, LicenseLimitSource.License)]
    [InlineData(Fixture.Unlimited, LicenseStage.Reverted, LicenseLimitCeiling.Count, 1, LicenseLimitSource.Community)]
    public Task ResolveAsync_TheConcurrentReviewsQuota_ResolvesOverTheFullMatrix(
        Fixture fixture,
        LicenseStage stage,
        LicenseLimitCeiling expectedCeiling,
        int? expectedCount,
        LicenseLimitSource expectedSource) =>
        this.AssertResolutionAsync(
            LicenseLimitKey.ConcurrentReviews,
            fixture,
            stage,
            expectedCeiling,
            expectedCount,
            expectedSource);

    // Authors per month: nothing counts the dimension without a license that states it, which is a different
    // answer from an unlimited ceiling on a dimension that is counted.
    [Theory]
    [InlineData(Fixture.None, LicenseStage.None, LicenseLimitCeiling.Unmetered, null, LicenseLimitSource.Community)]
    [InlineData(Fixture.Absent, LicenseStage.NotYetValid, LicenseLimitCeiling.Unmetered, null, LicenseLimitSource.Community)]
    [InlineData(Fixture.Absent, LicenseStage.Active, LicenseLimitCeiling.Unmetered, null, LicenseLimitSource.Community)]
    [InlineData(Fixture.Absent, LicenseStage.Warning, LicenseLimitCeiling.Unmetered, null, LicenseLimitSource.Community)]
    [InlineData(Fixture.Absent, LicenseStage.Grace, LicenseLimitCeiling.Unmetered, null, LicenseLimitSource.Community)]
    [InlineData(Fixture.Absent, LicenseStage.Reverted, LicenseLimitCeiling.Unmetered, null, LicenseLimitSource.Community)]
    [InlineData(Fixture.Number, LicenseStage.NotYetValid, LicenseLimitCeiling.Unmetered, null, LicenseLimitSource.Community)]
    [InlineData(Fixture.Number, LicenseStage.Active, LicenseLimitCeiling.Count, 7, LicenseLimitSource.License)]
    [InlineData(Fixture.Number, LicenseStage.Warning, LicenseLimitCeiling.Count, 7, LicenseLimitSource.License)]
    [InlineData(Fixture.Number, LicenseStage.Grace, LicenseLimitCeiling.Count, 7, LicenseLimitSource.License)]
    [InlineData(Fixture.Number, LicenseStage.Reverted, LicenseLimitCeiling.Unmetered, null, LicenseLimitSource.Community)]
    [InlineData(Fixture.Unlimited, LicenseStage.NotYetValid, LicenseLimitCeiling.Unmetered, null, LicenseLimitSource.Community)]
    [InlineData(Fixture.Unlimited, LicenseStage.Active, LicenseLimitCeiling.Unlimited, null, LicenseLimitSource.License)]
    [InlineData(Fixture.Unlimited, LicenseStage.Warning, LicenseLimitCeiling.Unlimited, null, LicenseLimitSource.License)]
    [InlineData(Fixture.Unlimited, LicenseStage.Grace, LicenseLimitCeiling.Unlimited, null, LicenseLimitSource.License)]
    [InlineData(Fixture.Unlimited, LicenseStage.Reverted, LicenseLimitCeiling.Unmetered, null, LicenseLimitSource.Community)]
    public Task ResolveAsync_TheAuthorsPerMonthQuota_ResolvesOverTheFullMatrix(
        Fixture fixture,
        LicenseStage stage,
        LicenseLimitCeiling expectedCeiling,
        int? expectedCount,
        LicenseLimitSource expectedSource) =>
        this.AssertResolutionAsync(
            LicenseLimitKey.AuthorsPerMonth,
            fixture,
            stage,
            expectedCeiling,
            expectedCount,
            expectedSource);

    // The licensed concurrent-review number counts only while running more than one review at a time is
    // available. Without it the answer is the community ceiling whatever the license states, including when the
    // license states no ceiling at all, so an admission decision cannot let through work the review pipeline
    // then runs one at a time.
    [Theory]
    [InlineData(Fixture.Number, true, LicenseLimitCeiling.Count, 7, LicenseLimitSource.License)]
    [InlineData(Fixture.Number, false, LicenseLimitCeiling.Count, 1, LicenseLimitSource.Community)]
    [InlineData(Fixture.Unlimited, true, LicenseLimitCeiling.Unlimited, null, LicenseLimitSource.License)]
    [InlineData(Fixture.Unlimited, false, LicenseLimitCeiling.Count, 1, LicenseLimitSource.Community)]
    public async Task ResolveAsync_ConcurrentReviews_TakesTheLicensedValueOnlyWhenParallelExecutionIsAvailable(
        Fixture fixture,
        bool parallelReviewExecution,
        LicenseLimitCeiling expectedCeiling,
        int? expectedCount,
        LicenseLimitSource expectedSource)
    {
        var licenseState = this.StateFor(fixture, LicenseLimitKey.ConcurrentReviews, LicenseStage.Active);
        var sut = CreateResolver(licenseState, parallelReviewExecution);

        var resolution = await sut.ResolveAsync(LicenseLimitKey.ConcurrentReviews);

        Assert.Equal(expectedCeiling, resolution.Ceiling);
        Assert.Equal((long?)expectedCount, resolution.Count);
        Assert.Equal(expectedSource, resolution.Source);
    }

    // The same rule over the real capability service, the real policy store and the real catalog, so the answer
    // depends on the resolution an operator's override reaches rather than on a configured substitute. The
    // license names the capability, which leaves the stored override as the only thing that can take it away.
    [Theory]
    [InlineData(PremiumCapabilityOverrideState.Default, 7, LicenseLimitSource.License)]
    [InlineData(PremiumCapabilityOverrideState.Disabled, 1, LicenseLimitSource.Community)]
    public async Task ResolveAsync_ConcurrentReviews_FollowsAStoredOverrideThroughTheCapabilityService(
        PremiumCapabilityOverrideState overrideState,
        int expectedCount,
        LicenseLimitSource expectedSource)
    {
        await using var db = CreateContext();
        await SeedOverrideAsync(db, PremiumCapabilityKey.ParallelReviewExecution, overrideState);

        var licenseState = this.VerifiedState(
            LicenseStage.Active,
            new LicenseLimits { ConcurrentReviews = LicenseLimit.Of(LicensedNumber) },
            PremiumCapabilityKey.ParallelReviewExecution);

        var catalog = new StaticPremiumCapabilityCatalog();
        var capabilityService = new LicensingCapabilityService(
            catalog,
            new LicensingPolicyRepository(db, catalog),
            new FixedLicenseStateProvider(licenseState),
            new LicensingIdentityRepository(db, TimeProvider.System));

        var resolution = await CreateResolver(licenseState, capabilityService)
            .ResolveAsync(LicenseLimitKey.ConcurrentReviews);

        Assert.Equal(LicenseLimitCeiling.Count, resolution.Ceiling);
        Assert.Equal(expectedCount, resolution.Count);
        Assert.Equal(expectedSource, resolution.Source);
    }

    // The interaction rule belongs to concurrent reviews alone. A licensed number of clients, runners or authors
    // stands whether or not reviews may run in parallel, and the capability is not consulted for them.
    [Theory]
    [InlineData(LicenseLimitKey.Clients)]
    [InlineData(LicenseLimitKey.Runners)]
    [InlineData(LicenseLimitKey.AuthorsPerMonth)]
    public async Task ResolveAsync_TheOtherQuotas_DoNotDependOnTheParallelExecutionCapability(LicenseLimitKey key)
    {
        var licenseState = this.StateFor(Fixture.Number, key, LicenseStage.Active);

        // The substitute refuses the capability, so a resolver that consulted it would answer the community
        // value and fail the assertions on the ceiling as well as the received-calls check.
        var capabilityService = CreateCapabilityService(parallelReviewExecution: false);

        var resolution = await CreateResolver(licenseState, capabilityService).ResolveAsync(key);

        Assert.Equal(LicenseLimitCeiling.Count, resolution.Ceiling);
        Assert.Equal(LicensedNumber, resolution.Count);
        Assert.Equal(LicenseLimitSource.License, resolution.Source);
        await capabilityService.DidNotReceive().IsEnabledAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // The community values, stated as the table that defines them. Clients are unconstrained, one review runs at
    // a time, no runner may enroll, and distinct authors are not counted at all.
    [Fact]
    public async Task ResolveAsync_WithoutALicense_HoldsEveryQuotaToItsCommunityValue()
    {
        var sut = CreateResolver(LicenseState.None());

        var clients = await sut.ResolveAsync(LicenseLimitKey.Clients);
        var concurrentReviews = await sut.ResolveAsync(LicenseLimitKey.ConcurrentReviews);
        var runners = await sut.ResolveAsync(LicenseLimitKey.Runners);
        var authorsPerMonth = await sut.ResolveAsync(LicenseLimitKey.AuthorsPerMonth);

        Assert.Equal(LicenseLimitCeiling.Unlimited, clients.Ceiling);
        Assert.Null(clients.Count);

        Assert.Equal(LicenseLimitCeiling.Count, concurrentReviews.Ceiling);
        Assert.Equal(1, concurrentReviews.Count);

        // The same number the review pipeline clamps both of its concurrency axes to without the capability, so
        // an admission decision and the width a host runs at cannot report different limits.
        Assert.Equal(ReviewConcurrencyPolicy.Unlicensed, concurrentReviews.Count);

        Assert.Equal(LicenseLimitCeiling.Count, runners.Ceiling);
        Assert.Equal(0, runners.Count);

        Assert.Equal(LicenseLimitCeiling.Unmetered, authorsPerMonth.Ceiling);
        Assert.Null(authorsPerMonth.Count);

        LicenseLimitResolution[] resolutions = [clients, concurrentReviews, runners, authorsPerMonth];
        Assert.All(resolutions, resolution => Assert.Equal(LicenseLimitSource.Community, resolution.Source));
        Assert.All(resolutions, resolution => Assert.Equal(LicenseStage.None, resolution.Stage));
    }

    // A dimension added to the limit keys without a community value has no answer to give, so the resolver
    // refuses rather than reporting a ceiling nobody decided on.
    [Fact]
    public async Task ResolveAsync_ADimensionTheResolverDoesNotKnow_IsRefusedWithoutALicense()
    {
        var sut = CreateResolver(LicenseState.None());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => sut.ResolveAsync((LicenseLimitKey)int.MaxValue));
    }

    // The licensed path reads the limit the license states for the dimension, and an unknown dimension has no
    // member to read either.
    [Fact]
    public async Task ResolveAsync_ADimensionTheResolverDoesNotKnow_IsRefusedWithALicenseInForce()
    {
        var sut = CreateResolver(this.VerifiedState(LicenseStage.Active, LicenseLimits.None));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => sut.ResolveAsync((LicenseLimitKey)int.MaxValue));
    }

    private static ILicensingCapabilityService CreateCapabilityService(bool parallelReviewExecution)
    {
        var capabilityService = Substitute.For<ILicensingCapabilityService>();
        capabilityService
            .IsEnabledAsync(PremiumCapabilityKey.ParallelReviewExecution, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(parallelReviewExecution));

        return capabilityService;
    }

    private static LicenseLimitResolver CreateResolver(
        LicenseState licenseState,
        bool parallelReviewExecution = true) =>
        CreateResolver(licenseState, CreateCapabilityService(parallelReviewExecution));

    private static LicenseLimitResolver CreateResolver(
        LicenseState licenseState,
        ILicensingCapabilityService capabilityService) =>
        new(new FixedLicenseStateProvider(licenseState), capabilityService);

    private static LicenseLimits LimitsFor(LicenseLimitKey key, LicenseLimit limit)
    {
        return key switch
        {
            LicenseLimitKey.AuthorsPerMonth => new LicenseLimits { AuthorsPerMonth = limit },
            LicenseLimitKey.Clients => new LicenseLimits { Clients = limit },
            LicenseLimitKey.Runners => new LicenseLimits { Runners = limit },
            LicenseLimitKey.ConcurrentReviews => new LicenseLimits { ConcurrentReviews = limit },
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "No limit member for this key."),
        };
    }

    /// <summary>A term whose window puts the instant the state is built at in the requested stage.</summary>
    private static (DateTimeOffset NotBefore, DateTimeOffset ExpiresAt) TermFor(LicenseStage stage)
    {
        return stage switch
        {
            LicenseStage.NotYetValid => (Now.AddDays(30), Now.AddDays(365)),
            LicenseStage.Active => (Now.AddDays(-30), Now.AddDays(365)),
            LicenseStage.Warning => (Now.AddDays(-30), Now.AddDays(10)),
            LicenseStage.Grace => (Now.AddDays(-30), Now.AddDays(-1)),
            LicenseStage.Reverted => (Now.AddDays(-30), Now.AddDays(-15)),
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "No term for this stage."),
        };
    }

    private static async Task SeedOverrideAsync(
        MeisterProPRDbContext db,
        string capabilityKey,
        PremiumCapabilityOverrideState overrideState)
    {
        if (overrideState == PremiumCapabilityOverrideState.Default)
        {
            return;
        }

        db.PremiumCapabilityOverrides.Add(
            new PremiumCapabilityOverrideRecord
            {
                CapabilityKey = capabilityKey,
                OverrideState = overrideState,
                UpdatedAt = Now,
            });

        await db.SaveChangesAsync();
    }

    private static MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"TestDb_LicenseLimitResolver_{Guid.NewGuid()}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new MeisterProPRDbContext(options);
    }

    private async Task AssertResolutionAsync(
        LicenseLimitKey key,
        Fixture fixture,
        LicenseStage stage,
        LicenseLimitCeiling expectedCeiling,
        int? expectedCount,
        LicenseLimitSource expectedSource)
    {
        var licenseState = this.StateFor(fixture, key, stage);
        var sut = CreateResolver(licenseState);

        var resolution = await sut.ResolveAsync(key);

        Assert.Equal(key, resolution.Key);
        Assert.Equal(expectedCeiling, resolution.Ceiling);
        Assert.Equal((long?)expectedCount, resolution.Count);
        Assert.Equal(expectedSource, resolution.Source);
        Assert.Equal(stage, resolution.Stage);
    }

    private LicenseState StateFor(Fixture fixture, LicenseLimitKey key, LicenseStage stage)
    {
        if (fixture == Fixture.None)
        {
            Assert.Equal(LicenseStage.None, stage);

            return LicenseState.None();
        }

        var limits = fixture switch
        {
            Fixture.Number => LimitsFor(key, LicenseLimit.Of(LicensedNumber)),
            Fixture.Unlimited => LimitsFor(key, LicenseLimit.Unlimited),
            _ => LicenseLimits.None,
        };

        var licenseState = this.VerifiedState(stage, limits);

        // The window the stage was requested by has to produce that stage, or the row would be asserting
        // something other than what it names.
        Assert.Equal(stage, licenseState.Stage);

        return licenseState;
    }

    private LicenseState VerifiedState(LicenseStage stage, LicenseLimits limits, params string[] capabilities)
    {
        var (notBefore, expiresAt) = TermFor(stage);
        var claims = LicenseTestChain.ClaimsFor(notBefore, expiresAt) with { Limits = limits };
        if (capabilities.Length > 0)
        {
            claims = claims with { Capabilities = capabilities };
        }

        var verification = this._verifier.Verify(this._chain.Sign(claims), Now);
        Assert.True(verification.IsVerified, verification.FailureDetail);

        return LicenseState.Verified(verification.License, Now, Now.AddDays(-2), null);
    }

    private sealed class FixedLicenseStateProvider(LicenseState state) : ILicenseStateProvider
    {
        public Task<LicenseState> GetStateAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(state);
        }

        public void Invalidate()
        {
        }
    }
}

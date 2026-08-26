// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Dtos;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Licensing.Queries.GetLicensingSummary;
using MeisterDev.ProPR.Application.Features.Licensing.Services;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.Persistence;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.Support;
using MeisterDev.ProPR.Licensing;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.Licensing;

/// <summary>
///     The administration read over the real capability service and the real limit resolver, so the numbers it
///     reports come from the same code an enforcement decision reads. License states are built by signing
///     documents with a chain generated for the test and verifying them with the product's own verifier.
/// </summary>
public sealed class GetLicensingSummaryHandlerTests : IDisposable
{
    private const long LicensedNumber = 7;

    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private static readonly LicensedResourceCounts Counts = new(4, 2, 1);

    private readonly LicenseTrustAnchor _anchor;
    private readonly LicenseTestChain _chain;
    private readonly MeisterProPRDbContext _db;
    private readonly LicenseVerifier _verifier;

    public GetLicensingSummaryHandlerTests()
    {
        this._chain = LicenseTestChain.Create();
        this._anchor = this._chain.CreateAnchor();
        this._verifier = new LicenseVerifier(this._anchor);
        this._db = CreateContext();
    }

    /// <summary>What the license on file states for every quota.</summary>
    public enum Fixture
    {
        /// <summary>No license is on file.</summary>
        None = 0,

        /// <summary>A verified license that states nothing for any quota.</summary>
        Absent = 1,

        /// <summary>A verified license that states a count for every quota.</summary>
        Number = 2,

        /// <summary>A verified license that states every quota as unlimited.</summary>
        Unlimited = 3,
    }

    /// <summary>
    ///     Every key the contract carries, read from the enum rather than listed, so a key added to it is
    ///     covered without this file being edited.
    /// </summary>
    public static TheoryData<LicenseLimitKey> EveryLimitKey { get; } = [.. Enum.GetValues<LicenseLimitKey>()];

    public void Dispose()
    {
        this._db.Dispose();
        this._anchor.Dispose();
        this._chain.Dispose();
    }

    // The whole license matrix, asserted against the resolver rather than against a second copy of the ceiling
    // table. What the panel reports and what an enforcement point resolves have to be one answer, so restating
    // the numbers here would let the two drift while both suites stayed green.
    [Theory]
    [InlineData(Fixture.None, LicenseStage.None)]
    [InlineData(Fixture.Absent, LicenseStage.NotYetValid)]
    [InlineData(Fixture.Absent, LicenseStage.Active)]
    [InlineData(Fixture.Absent, LicenseStage.Warning)]
    [InlineData(Fixture.Absent, LicenseStage.Grace)]
    [InlineData(Fixture.Absent, LicenseStage.Reverted)]
    [InlineData(Fixture.Number, LicenseStage.NotYetValid)]
    [InlineData(Fixture.Number, LicenseStage.Active)]
    [InlineData(Fixture.Number, LicenseStage.Warning)]
    [InlineData(Fixture.Number, LicenseStage.Grace)]
    [InlineData(Fixture.Number, LicenseStage.Reverted)]
    [InlineData(Fixture.Unlimited, LicenseStage.NotYetValid)]
    [InlineData(Fixture.Unlimited, LicenseStage.Active)]
    [InlineData(Fixture.Unlimited, LicenseStage.Warning)]
    [InlineData(Fixture.Unlimited, LicenseStage.Grace)]
    [InlineData(Fixture.Unlimited, LicenseStage.Reverted)]
    public async Task HandleAsync_OverTheLicenseMatrix_ReportsWhatTheResolverResolves(
        Fixture fixture,
        LicenseStage stage)
    {
        var licenseState = this.StateFor(fixture, stage);
        var resolver = this.CreateResolver(licenseState);
        var summary = await this.CreateHandler(licenseState, resolver).HandleAsync(new GetLicensingSummaryQuery());

        Assert.NotNull(summary.Limits);
        Assert.Equal(4, summary.Limits.Count);

        foreach (var limit in summary.Limits)
        {
            var resolution = await resolver.ResolveAsync(limit.Key);

            Assert.Equal(resolution.Ceiling, limit.EffectiveCeiling);
            Assert.Equal(resolution.Count, limit.EffectiveCount);
            Assert.Equal(resolution.Source, limit.EffectiveSource);
        }
    }

    // Without a license every quota is held to its community value, and the source says so for all four. The
    // license side of the payload reports nothing stated, because there is no document to state anything.
    [Fact]
    public async Task HandleAsync_WithoutALicense_ReportsEveryQuotaAtItsCommunityValueFromTheCommunitySource()
    {
        var summary = await this.CreateHandler(LicenseState.None()).HandleAsync(new GetLicensingSummaryQuery());

        Assert.NotNull(summary.Limits);
        Assert.All(summary.Limits, limit => Assert.Equal(LicenseLimitAllowance.Absent, limit.Allowance));
        Assert.All(summary.Limits, limit => Assert.Equal(LicenseLimitSource.Community, limit.EffectiveSource));

        var clients = LimitFor(summary, LicenseLimitKey.Clients);
        Assert.Equal(LicenseLimitCeiling.Unlimited, clients.EffectiveCeiling);
        Assert.Null(clients.EffectiveCount);

        var runners = LimitFor(summary, LicenseLimitKey.Runners);
        Assert.Equal(LicenseLimitCeiling.Count, runners.EffectiveCeiling);
        Assert.Equal(0, runners.EffectiveCount);

        var concurrentReviews = LimitFor(summary, LicenseLimitKey.ConcurrentReviews);
        Assert.Equal(LicenseLimitCeiling.Count, concurrentReviews.EffectiveCeiling);
        Assert.Equal(ReviewConcurrencyPolicy.Unlicensed, concurrentReviews.EffectiveCount);

        // Nothing counts distinct authors, which is a different answer from a counted dimension held to no
        // ceiling, and it carries no number either way.
        var authors = LimitFor(summary, LicenseLimitKey.AuthorsPerMonth);
        Assert.Equal(LicenseLimitCeiling.Unmetered, authors.EffectiveCeiling);
        Assert.Null(authors.EffectiveCount);
    }

    // The divergence an operator has to be able to read: the license states nothing for runners, the payload
    // reports it as unstated, and the ceiling an enrollment is refused against is the community value.
    [Fact]
    public async Task HandleAsync_ALimitTheLicenseLeavesOut_ReportsItAsAbsentAndEnforcedAtTheCommunityValue()
    {
        var licenseState = this.VerifiedState(LicenseStage.Active, LicenseLimits.None);

        var summary = await this.CreateHandler(licenseState).HandleAsync(new GetLicensingSummaryQuery());

        var runners = LimitFor(summary, LicenseLimitKey.Runners);
        Assert.Equal(LicenseLimitAllowance.Absent, runners.Allowance);
        Assert.Null(runners.LicensedCount);
        Assert.Equal(LicenseLimitCeiling.Count, runners.EffectiveCeiling);
        Assert.Equal(0, runners.EffectiveCount);
        Assert.Equal(LicenseLimitSource.Community, runners.EffectiveSource);
    }

    // The second divergence: past the grace window the document still states its number, and the panel still
    // reports it, while the ceiling in force is the community value. An operator reading a refusal sees both.
    [Fact]
    public async Task HandleAsync_PastTheGraceWindow_ReportsTheStatedNumberBesideTheCommunityCeiling()
    {
        var licenseState = this.VerifiedState(
            LicenseStage.Reverted,
            new LicenseLimits { Runners = LicenseLimit.Of(LicensedNumber) });

        var summary = await this.CreateHandler(licenseState).HandleAsync(new GetLicensingSummaryQuery());

        var runners = LimitFor(summary, LicenseLimitKey.Runners);
        Assert.Equal(LicenseLimitAllowance.Count, runners.Allowance);
        Assert.Equal(LicensedNumber, runners.LicensedCount);
        Assert.Equal(LicenseLimitCeiling.Count, runners.EffectiveCeiling);
        Assert.Equal(0, runners.EffectiveCount);
        Assert.Equal(LicenseLimitSource.Community, runners.EffectiveSource);
    }

    [Fact]
    public async Task HandleAsync_ALicensedNumber_ReportsItAsTheCeilingFromTheLicense()
    {
        var licenseState = this.VerifiedState(
            LicenseStage.Active,
            new LicenseLimits { Clients = LicenseLimit.Of(LicensedNumber) });

        var summary = await this.CreateHandler(licenseState).HandleAsync(new GetLicensingSummaryQuery());

        var clients = LimitFor(summary, LicenseLimitKey.Clients);
        Assert.Equal(LicensedNumber, clients.LicensedCount);
        Assert.Equal(LicenseLimitCeiling.Count, clients.EffectiveCeiling);
        Assert.Equal(LicensedNumber, clients.EffectiveCount);
        Assert.Equal(LicenseLimitSource.License, clients.EffectiveSource);
    }

    [Fact]
    public async Task HandleAsync_ALicensedUnlimitedLimit_ReportsAnUnlimitedCeilingFromTheLicense()
    {
        var licenseState = this.VerifiedState(
            LicenseStage.Active,
            new LicenseLimits { Runners = LicenseLimit.Unlimited });

        var summary = await this.CreateHandler(licenseState).HandleAsync(new GetLicensingSummaryQuery());

        var runners = LimitFor(summary, LicenseLimitKey.Runners);
        Assert.Equal(LicenseLimitAllowance.Unlimited, runners.Allowance);
        Assert.Equal(LicenseLimitCeiling.Unlimited, runners.EffectiveCeiling);
        Assert.Null(runners.EffectiveCount);
        Assert.Equal(LicenseLimitSource.License, runners.EffectiveSource);
    }

    // A licensed number that a capability gates is reported at the value in force, not at the value stated.
    // Without the capability that allows more than one review at a time the installation runs one at a time
    // whatever the license says, so reporting the licensed number as the ceiling would name a number no claim
    // enforces.
    [Fact]
    public async Task HandleAsync_ALicensedNumberWhoseCapabilityIsNotGranted_ReportsTheCommunityCeiling()
    {
        var licenseState = this.VerifiedState(
            LicenseStage.Active,
            new LicenseLimits { ConcurrentReviews = LicenseLimit.Of(6) },
            PremiumCapabilityKey.MultipleScmProviders);

        var summary = await this.CreateHandler(licenseState).HandleAsync(new GetLicensingSummaryQuery());

        var concurrentReviews = LimitFor(summary, LicenseLimitKey.ConcurrentReviews);
        Assert.Equal(6, concurrentReviews.LicensedCount);
        Assert.Equal(LicenseLimitCeiling.Count, concurrentReviews.EffectiveCeiling);
        Assert.Equal(ReviewConcurrencyPolicy.Unlicensed, concurrentReviews.EffectiveCount);
        Assert.Equal(LicenseLimitSource.Community, concurrentReviews.EffectiveSource);
    }

    // Every key the contract carries has to resolve. A key added to the enum without a community value would
    // otherwise turn the panel read into a server error rather than reporting the dimension it names.
    [Theory]
    [MemberData(nameof(EveryLimitKey))]
    public async Task HandleAsync_EveryLimitKey_ResolvesThroughTheRealResolver(LicenseLimitKey key)
    {
        var resolver = this.CreateResolver(LicenseState.None());

        var resolution = await resolver.ResolveAsync(key);
        var summary = await this.CreateHandler(LicenseState.None(), resolver)
            .HandleAsync(new GetLicensingSummaryQuery());

        Assert.Equal(key, resolution.Key);
        Assert.Equal(resolution.Ceiling, LimitFor(summary, key).EffectiveCeiling);
    }

    // Three counts come from the count source the enforcement points admit against. Authors come from the
    // rollup instead, which this composition does not have, so that row carries no number here.
    [Fact]
    public async Task HandleAsync_ReportsTheCountsFromTheCountSource()
    {
        var summary = await this.CreateHandler(LicenseState.None()).HandleAsync(new GetLicensingSummaryQuery());

        Assert.Equal(Counts.Clients, LimitFor(summary, LicenseLimitKey.Clients).InformationalCount);
        Assert.Equal(Counts.EnrolledRunners, LimitFor(summary, LicenseLimitKey.Runners).InformationalCount);
        Assert.Equal(
            Counts.ReviewsInProgress,
            LimitFor(summary, LicenseLimitKey.ConcurrentReviews).InformationalCount);
        Assert.Null(LimitFor(summary, LicenseLimitKey.AuthorsPerMonth).InformationalCount);
    }

    // The authors row is counted from the rollup, and the identities the exclusion rules kept out of the month
    // are reported beside that count. Both come from the same read, so the panel can show what the count left
    // out.
    [Fact]
    public async Task HandleAsync_ReportsTheAuthorCountAndTheExcludedCountFromTheRollup()
    {
        var rollup = Substitute.For<IAuthorActivityRollupStore>();
        rollup.GetCurrentMonthCountsAsync(Arg.Any<CancellationToken>()).Returns(new AuthorActivityMonthCounts(new DateOnly(2026, 8, 1), 11, 3));

        var summary = await this.CreateHandler(LicenseState.None(), rollup)
            .HandleAsync(new GetLicensingSummaryQuery());

        var authors = LimitFor(summary, LicenseLimitKey.AuthorsPerMonth);
        Assert.Equal(11, authors.InformationalCount);
        Assert.Equal(3, authors.ExcludedAutomationCount);
    }

    // The exclusion is reported on the authors row alone, because no other dimension excludes anything, and
    // the counts of the other three stay the count source's.
    [Fact]
    public async Task HandleAsync_ReportsNoExclusionOrRollupCountOnTheOtherRows()
    {
        var rollup = Substitute.For<IAuthorActivityRollupStore>();
        rollup.GetCurrentMonthCountsAsync(Arg.Any<CancellationToken>()).Returns(new AuthorActivityMonthCounts(new DateOnly(2026, 8, 1), 11, 3));

        var summary = await this.CreateHandler(LicenseState.None(), rollup)
            .HandleAsync(new GetLicensingSummaryQuery());

        Assert.Null(LimitFor(summary, LicenseLimitKey.Clients).ExcludedAutomationCount);
        Assert.Null(LimitFor(summary, LicenseLimitKey.Runners).ExcludedAutomationCount);
        Assert.Null(LimitFor(summary, LicenseLimitKey.ConcurrentReviews).ExcludedAutomationCount);
        Assert.Equal(Counts.Clients, LimitFor(summary, LicenseLimitKey.Clients).InformationalCount);
    }

    // A host with no rollup to read reports neither number. Reporting zero for the count would read as an
    // installation nobody opened a pull request on, and zero for the exclusion would state that the rules ran
    // and kept nobody out.
    [Fact]
    public async Task HandleAsync_OnAHostWithoutARollup_ReportsNeitherAuthorNumber()
    {
        var summary = await this.CreateHandler(LicenseState.None()).HandleAsync(new GetLicensingSummaryQuery());

        var authors = LimitFor(summary, LicenseLimitKey.AuthorsPerMonth);
        Assert.Null(authors.InformationalCount);
        Assert.Null(authors.ExcludedAutomationCount);
    }

    // The panel and the refusal have to name one number. The claim site turns the resolved limit into the
    // ceiling it enforces and quotes that ceiling in the text an operator reads, so the text has to carry the
    // number and the observed count the panel shows.
    [Fact]
    public async Task HandleAsync_TheConcurrentReviewCeiling_IsTheNumberARefusalNames()
    {
        var licenseState = this.VerifiedState(
            LicenseStage.Active,
            new LicenseLimits { ConcurrentReviews = LicenseLimit.Of(LicensedNumber) },
            PremiumCapabilityKey.ParallelReviewExecution);
        var resolver = this.CreateResolver(licenseState);

        var summary = await this.CreateHandler(licenseState, resolver).HandleAsync(new GetLicensingSummaryQuery());
        var reported = LimitFor(summary, LicenseLimitKey.ConcurrentReviews);

        var enforced = ConcurrentReviewCeiling.From(await resolver.ResolveAsync(LicenseLimitKey.ConcurrentReviews));

        Assert.NotNull(enforced);
        Assert.Equal(reported.EffectiveCount, enforced.Cap);
        Assert.Equal(reported.EffectiveSource, enforced.Source);

        var refusal = enforced.Describe((int)reported.InformationalCount!.Value);
        Assert.Contains($"running {reported.InformationalCount} of {reported.EffectiveCount}", refusal, StringComparison.Ordinal);
    }

    // A host that reports no license state at all sends no limits, and the read has nothing to resolve or
    // count for them. Resolving anyway would ask for a license state the host does not have.
    [Fact]
    public async Task HandleAsync_ASummaryWithoutLimits_ResolvesNothingAndCountsNothing()
    {
        var capabilityService = Substitute.For<ILicensingCapabilityService>();
        capabilityService.GetSummaryAsync(Arg.Any<CancellationToken>())
            .Returns(new LicensingSummaryDto(InstallationEdition.Community, null, []));
        var resolver = Substitute.For<ILicenseLimitResolver>();
        var countSource = Substitute.For<ILicensedResourceCountSource>();

        var summary = await new GetLicensingSummaryHandler(capabilityService, countSource, resolver)
            .HandleAsync(new GetLicensingSummaryQuery());

        Assert.Null(summary.Limits);
        await resolver.DidNotReceive().ResolveAsync(Arg.Any<LicenseLimitKey>(), Arg.Any<CancellationToken>());
        await countSource.DidNotReceive().GetCountsAsync(Arg.Any<CancellationToken>());
    }

    // The read evaluates the allowance as well, so the panel reports the month as it is now rather than as the
    // last lifecycle sweep left it.
    [Fact]
    public async Task HandleAsync_AMonthAboveTheLicensedAuthorNumber_ReportsTheOverage()
    {
        var licenseState = this.VerifiedState(
            LicenseStage.Active,
            new LicenseLimits { AuthorsPerMonth = LicenseLimit.Of(LicensedNumber) });

        var summary = await this.CreateHandler(licenseState, RollupWith(counted: 12))
            .HandleAsync(new GetLicensingSummaryQuery());

        Assert.NotNull(summary.AuthorOverage);
        Assert.True(summary.AuthorOverage.IsInOverage);
        Assert.Equal(LicensedNumber, summary.AuthorOverage.LicensedCount);
        Assert.Equal(12, summary.AuthorOverage.ObservedCount);
    }

    // The count is below the number, so the payload says the installation is not above it. The row an earlier
    // month wrote is history and is not what this field is derived from.
    [Fact]
    public async Task HandleAsync_AMonthBelowTheLicensedAuthorNumber_ReportsNotInOverage()
    {
        var licenseState = this.VerifiedState(
            LicenseStage.Active,
            new LicenseLimits { AuthorsPerMonth = LicenseLimit.Of(LicensedNumber) });

        var summary = await this.CreateHandler(licenseState, RollupWith(counted: 2))
            .HandleAsync(new GetLicensingSummaryQuery());

        Assert.NotNull(summary.AuthorOverage);
        Assert.False(summary.AuthorOverage.IsInOverage);
        Assert.Equal(2, summary.AuthorOverage.ObservedCount);
    }

    // The three readings with no number to compare against carry no overage field at all, which is a different
    // answer from reporting an installation that is inside a number nobody stated.
    [Theory]
    [InlineData(Fixture.None)]
    [InlineData(Fixture.Absent)]
    [InlineData(Fixture.Unlimited)]
    public async Task HandleAsync_WithNoLicensedAuthorNumber_ReportsNoOverage(Fixture fixture)
    {
        var licenseState = fixture == Fixture.None
            ? LicenseState.None()
            : this.StateFor(fixture, LicenseStage.Active);

        var summary = await this.CreateHandler(licenseState, RollupWith(counted: 40))
            .HandleAsync(new GetLicensingSummaryQuery());

        Assert.Null(summary.AuthorOverage);
    }

    // The read hands the count it already took to the evaluation, so one request counts the month once rather
    // than once for the panel row and once for the comparison.
    [Fact]
    public async Task HandleAsync_CountsTheMonthOnceForTheRowAndTheEvaluation()
    {
        var licenseState = this.VerifiedState(
            LicenseStage.Active,
            new LicenseLimits { AuthorsPerMonth = LicenseLimit.Of(LicensedNumber) });
        var rollup = RollupWith(counted: 12);

        await this.CreateHandler(licenseState, rollup).HandleAsync(new GetLicensingSummaryQuery());

        await rollup.Received(1).GetCurrentMonthCountsAsync(Arg.Any<CancellationToken>());
        await rollup.DidNotReceive().CountCurrentMonthAuthorsAsync(Arg.Any<CancellationToken>());
        await rollup.DidNotReceive().CountCurrentMonthExcludedAuthorsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ReportsTheTrailingYearPeakFromTheRollup()
    {
        var peak = new AuthorMonthCount(new DateOnly(2026, 4, 1), 19);
        var rollup = RollupWith(counted: 3);
        rollup.GetTrailingYearPeakAsync(Arg.Any<CancellationToken>()).Returns(peak);

        var summary = await this.CreateHandler(LicenseState.None(), rollup)
            .HandleAsync(new GetLicensingSummaryQuery());

        Assert.NotNull(summary.AuthorPeakMonth);
        Assert.Equal(peak.Month, summary.AuthorPeakMonth.Month);
        Assert.Equal(peak.AuthorCount, summary.AuthorPeakMonth.AuthorCount);
    }

    [Fact]
    public async Task HandleAsync_WithNoCountedAuthorInTheWindow_ReportsNoPeak()
    {
        var summary = await this.CreateHandler(LicenseState.None(), RollupWith(counted: 0))
            .HandleAsync(new GetLicensingSummaryQuery());

        Assert.Null(summary.AuthorPeakMonth);
    }

    // A host with neither a rollup nor an evaluation to run reports neither field rather than reporting a month
    // it cannot measure.
    [Fact]
    public async Task HandleAsync_OnAHostWithoutARollup_ReportsNeitherOverageNorPeak()
    {
        var summary = await this.CreateHandler(LicenseState.None()).HandleAsync(new GetLicensingSummaryQuery());

        Assert.Null(summary.AuthorOverage);
        Assert.Null(summary.AuthorPeakMonth);
    }

    private static IAuthorActivityRollupStore RollupWith(int counted)
    {
        var rollup = Substitute.For<IAuthorActivityRollupStore>();
        rollup.GetCurrentMonthCountsAsync(Arg.Any<CancellationToken>()).Returns(new AuthorActivityMonthCounts(new DateOnly(2026, 8, 1), counted, 0));

        return rollup;
    }

    private static LicenseLimitDto LimitFor(LicensingSummaryDto summary, LicenseLimitKey key) =>
        Assert.Single(summary.Limits!.Where(limit => limit.Key == key));

    private static ILicensedResourceCountSource CreateCountSource()
    {
        var countSource = Substitute.For<ILicensedResourceCountSource>();
        countSource.GetCountsAsync(Arg.Any<CancellationToken>()).Returns(Counts);

        return countSource;
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

    private static LicenseLimits LimitsFor(LicenseLimit limit) => new()
    {
        AuthorsPerMonth = limit,
        Clients = limit,
        Runners = limit,
        ConcurrentReviews = limit,
    };

    private static MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"TestDb_GetLicensingSummary_{Guid.NewGuid()}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new MeisterProPRDbContext(options);
    }

    private LicensingCapabilityService CreateCapabilityService(LicenseState licenseState)
    {
        var catalog = new StaticPremiumCapabilityCatalog();

        return new LicensingCapabilityService(
            catalog,
            new LicensingPolicyRepository(this._db, catalog),
            new FixedLicenseStateProvider(licenseState),
            new LicensingIdentityRepository(this._db, TimeProvider.System));
    }

    private LicenseLimitResolver CreateResolver(LicenseState licenseState) =>
        new(new FixedLicenseStateProvider(licenseState), this.CreateCapabilityService(licenseState));

    private GetLicensingSummaryHandler CreateHandler(LicenseState licenseState) =>
        this.CreateHandler(licenseState, this.CreateResolver(licenseState));

    private GetLicensingSummaryHandler CreateHandler(LicenseState licenseState, ILicenseLimitResolver resolver) =>
        new(this.CreateCapabilityService(licenseState), CreateCountSource(), resolver);

    /// <summary>
    ///     The read over a host that has a rollup, with the real evaluation composed on the same license state.
    ///     The allowance comparison the payload reports therefore comes from the code the lifecycle sweep runs
    ///     rather than from a second copy of the rule.
    /// </summary>
    private GetLicensingSummaryHandler CreateHandler(
        LicenseState licenseState,
        IAuthorActivityRollupStore rollupStore) =>
        new(
            this.CreateCapabilityService(licenseState),
            CreateCountSource(),
            this.CreateResolver(licenseState),
            rollupStore,
            new AuthorOverageEvaluator(
                new FixedLicenseStateProvider(licenseState),
                rollupStore,
                Substitute.For<IAuthorOverageStore>(),
                Substitute.For<ILogger<AuthorOverageEvaluator>>()));

    private LicenseState StateFor(Fixture fixture, LicenseStage stage)
    {
        if (fixture == Fixture.None)
        {
            Assert.Equal(LicenseStage.None, stage);

            return LicenseState.None();
        }

        var limits = fixture switch
        {
            Fixture.Number => LimitsFor(LicenseLimit.Of(LicensedNumber)),
            Fixture.Unlimited => LimitsFor(LicenseLimit.Unlimited),
            _ => LicenseLimits.None,
        };

        var licenseState = this.VerifiedState(stage, limits, PremiumCapabilityKey.ParallelReviewExecution);

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

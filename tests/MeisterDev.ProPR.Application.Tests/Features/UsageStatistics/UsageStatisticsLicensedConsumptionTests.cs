// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Models;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Ports;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Services;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Support;
using MeisterDev.ProPR.Licensing;
using MeisterDev.ProPR.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.UsageStatistics;

/// <summary>
///     What a commercial installation reports about its license, and what a community installation keeps
///     reporting once those fields exist.
/// </summary>
public sealed class UsageStatisticsLicensedConsumptionTests : IDisposable
{
    /// <summary>The payload a community installation sends, with the counts and version the cases below use.</summary>
    private const string CommunityPayload =
        """{"schemaVersion":1,"instanceId":"11111111-2222-3333-4444-555555555555","productVersion":"1.2.3","edition":"community","activeUsers":"2-5","pullRequestsPerWeek":"1-20","findingsRaisedPerWeek":"1-50","findingsAcceptedPerWeek":"1-50","findingsDismissedPerWeek":"1-50"}""";

    private const string ProfileHash = "2f0d9a71c48b3e5602d17ac9fb84e3d5a6c012bf7d94e8315a0bc6d729f4e81c";

    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NotBefore = Now.AddYears(-1);
    private static readonly DateTimeOffset ExpiresAt = Now.AddDays(60);

    private static readonly Guid LicensingIdentity = Guid.Parse("4f6b1d02-9c58-4f7a-8f2e-1d3c5b7a9e04");
    private static readonly LicensedResourceCounts Counts = new(12, 4, 2);

    /// <summary>
    ///     The highest number of reviews that executed at the same time on the previous UTC day, which is what
    ///     the report carries for that dimension rather than the two executing right now.
    /// </summary>
    private const long PreviousDayPeak = 6;

    /// <summary>Distinct authors the current UTC month holds, which is the count the report carries.</summary>
    private const long CurrentMonthAuthors = 37;

    /// <summary>
    ///     Authors the same month holds as automation. They are reported to an operator beside the counted
    ///     ones, and they are no part of what the installation consumes, so they are not sent.
    /// </summary>
    private const long CurrentMonthExcludedAuthors = 9;

    private static readonly UsageStatisticsCounts ActivityCounts = new(3, 4, 5, 6, 7);

    private readonly LicenseTrustAnchor _anchor;
    private readonly LicenseTestChain _chain;
    private readonly LicenseVerifier _verifier;

    public UsageStatisticsLicensedConsumptionTests()
    {
        this._chain = LicenseTestChain.Create();
        this._anchor = this._chain.CreateAnchor();
        this._verifier = new LicenseVerifier(this._anchor);
    }

    public void Dispose()
    {
        this._anchor.Dispose();
        this._chain.Dispose();
    }

    [Fact]
    public async Task ACommercialSnapshot_CarriesTheLicenseTheIdentityTheProfileAndTheCounts()
    {
        var snapshot = await this.BuildAsync(this.StateAt(Now));

        Assert.Equal(LicenseTestChain.ClaimsFor(NotBefore, ExpiresAt).LicenseId, snapshot.LicenseId);
        Assert.Equal(LicensingIdentity, snapshot.LicensingIdentity);
        Assert.Equal(ProfileHash, snapshot.SystemProfileHash);
        Assert.Equal(12, snapshot.ConsumedClients);
        Assert.Equal(4, snapshot.ConsumedRunners);
        Assert.Equal(PreviousDayPeak, snapshot.PeakConcurrentReviews);
        Assert.Equal(CurrentMonthAuthors, snapshot.ConsumedAuthorsPerMonth);
    }

    // The receiver reads the wire names, so each one is asserted against the serialized payload rather than
    // against the property it came from.
    [Fact]
    public async Task ACommercialPayload_CarriesEachLicensingValueUnderItsWireName()
    {
        var payload = UsageStatisticsContract.Serialize(await this.BuildAsync(this.StateAt(Now)));

        Assert.Contains("\"licenseId\":\"0f4c1b7a-6d21-4f36-9e18-5a7b3c9d2e40\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"licensingIdentity\":\"4f6b1d02-9c58-4f7a-8f2e-1d3c5b7a9e04\"", payload, StringComparison.Ordinal);
        Assert.Contains($"\"systemProfileHash\":\"{ProfileHash}\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"consumedClients\":12", payload, StringComparison.Ordinal);
        Assert.Contains("\"consumedRunners\":4", payload, StringComparison.Ordinal);
        Assert.Contains("\"peakConcurrentReviews\":6", payload, StringComparison.Ordinal);

        // The counted authors of the month. The same read also returns the excluded identities, as a
        // different number, so a payload carrying those instead fails here.
        Assert.Contains("\"consumedAuthorsPerMonth\":37", payload, StringComparison.Ordinal);
    }

    // A community installation's payload has to stay the payload it sent before the licensing fields existed,
    // down to the bytes. The resolver here would report a license, and the community edition stops it.
    [Fact]
    public async Task ACommunityPayload_IsTheSamePayloadItWasBeforeTheLicensingFieldsExisted()
    {
        var snapshot = await this.Builder(this.Resolver(this.StateAt(Now))).BuildAsync(
            UsageStatisticsTestDoubles.EnabledState(Now),
            UsageStatisticsEdition.Community);

        Assert.Equal(CommunityPayload, UsageStatisticsContract.Serialize(snapshot));
    }

    // Absent rather than null: a receiver that does not know a field has to see the payload it already knows.
    [Fact]
    public async Task AnInstallationWithNoLicenseInForce_LeavesTheLicensingFieldsOutOfThePayload()
    {
        var payload = UsageStatisticsContract.Serialize(await this.BuildAsync(LicenseState.None()));

        Assert.DoesNotContain("licenseId", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("licensingIdentity", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("systemProfileHash", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("consumed", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("peakConcurrentReviews", payload, StringComparison.Ordinal);
    }

    // The warning window and the grace window both keep the installation entitled, so both keep reporting what
    // they consume. Past the grace window the installation is on the community edition and reports nothing.
    [Theory]
    [InlineData(-366, false)]
    [InlineData(0, true)]
    [InlineData(45, true)]
    [InlineData(61, true)]
    [InlineData(75, false)]
    public async Task TheLicensingFields_FollowTheStagesThatKeepTheInstallationEntitled(
        int daysFromNow,
        bool expectedToReport)
    {
        var evaluatedAt = Now.AddDays(daysFromNow);
        var licenseState = this.StateAt(evaluatedAt);

        var snapshot = await this.BuildAsync(licenseState);

        Assert.Equal(expectedToReport, snapshot.LicenseId is not null);
        Assert.Equal(expectedToReport, snapshot.ConsumedClients is not null);
    }

    // Which licensing read fails does not matter. The values are reported, not enforced, so a failure leaves a
    // payload of the shape a community installation sends rather than stopping the snapshot.
    [Theory]
    [InlineData(nameof(ILicenseStateProvider))]
    [InlineData(nameof(ILicensingIdentityStore))]
    [InlineData(nameof(ISystemProfileStore))]
    [InlineData(nameof(ILicensedResourceCountSource))]
    [InlineData(nameof(IConcurrentReviewPeakStore))]
    [InlineData(nameof(IAuthorActivityRollupStore))]
    public async Task ALicensingReadThatFails_LeavesTheFieldsOutAndTheRestOfTheSnapshotIntact(string failingRead)
    {
        var snapshot = await this.Builder(this.ResolverWithFailingRead(failingRead)).BuildAsync(
            UsageStatisticsTestDoubles.EnabledState(Now),
            UsageStatisticsEdition.Commercial);

        Assert.Null(snapshot.LicenseId);
        Assert.Null(snapshot.LicensingIdentity);
        Assert.Null(snapshot.SystemProfileHash);
        Assert.Null(snapshot.ConsumedClients);
        Assert.Null(snapshot.ConsumedRunners);
        Assert.Null(snapshot.PeakConcurrentReviews);
        Assert.Null(snapshot.ConsumedAuthorsPerMonth);
        Assert.Equal("2-5", snapshot.ActiveUsers);
        Assert.Equal(UsageStatisticsEdition.Commercial, snapshot.Edition);
    }

    // The snapshot has to reach the receiver whatever the licensing reads did, because the cycle that carries
    // it also carries the version the response answers about.
    [Fact]
    public async Task ALicensingReadThatFails_DoesNotStopTheSnapshotFromBeingSent()
    {
        var state = UsageStatisticsTestDoubles.EnabledState(Now);
        var store = Substitute.For<IUsageStatisticsStateStore>();
        store.GetAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(state));
        store.TryClaimSendAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        store.RecordSendOutcomeAsync(Arg.Any<UsageStatisticsSendOutcome>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(state));

        var pingClient = Substitute.For<IUsageStatisticsPingClient>();
        pingClient.SendAsync(Arg.Any<UsageStatisticsSnapshot>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new UsageStatisticsSendOutcome(Now, true, "Delivered.", null)));

        var sender = new UsageStatisticsSender(
            store,
            this.Builder(this.ResolverWithFailingRead(nameof(ILicenseStateProvider))),
            UsageStatisticsTestDoubles.EditionResolver(InstallationEdition.Commercial),
            pingClient,
            new FakeTimeProvider(Now));

        var result = await sender.SendIfDueAsync();

        Assert.Equal(UsageStatisticsSendDecision.Sent, result.Decision);
        await pingClient.Received(1).SendAsync(
            Arg.Is<UsageStatisticsSnapshot>(snapshot => snapshot.LicenseId == null),
            Arg.Any<CancellationToken>());
    }

    // An installation whose profile has not been captured yet has no hash to report. Consumption is attributed
    // to the two identifiers, so they and the counts are still reported.
    [Fact]
    public async Task AnInstallationWithNoRecordedProfile_ReportsTheRestWithoutTheHash()
    {
        var snapshot = await this.Builder(this.Resolver(this.StateAt(Now), profileHash: null)).BuildAsync(
            UsageStatisticsTestDoubles.EnabledState(Now),
            UsageStatisticsEdition.Commercial);

        Assert.Null(snapshot.SystemProfileHash);
        Assert.NotNull(snapshot.LicenseId);
        Assert.Equal(LicensingIdentity, snapshot.LicensingIdentity);
        Assert.Equal(12, snapshot.ConsumedClients);
    }

    // A day on which no review executed leaves no record, and the report says nothing for that dimension. A
    // zero would be indistinguishable from an installation that does not measure it.
    [Fact]
    public async Task APreviousDayWithNoRecordedPeak_LeavesThatFieldOutOfThePayload()
    {
        var snapshot = await this.Builder(this.Resolver(this.StateAt(Now), previousDayPeak: null)).BuildAsync(
            UsageStatisticsTestDoubles.EnabledState(Now),
            UsageStatisticsEdition.Commercial);

        Assert.Null(snapshot.PeakConcurrentReviews);
        Assert.Equal(12, snapshot.ConsumedClients);
        Assert.DoesNotContain(
            "peakConcurrentReviews",
            UsageStatisticsContract.Serialize(snapshot),
            StringComparison.Ordinal);
    }

    // An installation with no rollup has no author measurement at all, which is not the same as a month that
    // holds no author. The field is left out rather than reported as zero, and the rest is still reported.
    [Fact]
    public async Task AnInstallationWithNoAuthorRollup_LeavesThatFieldOutOfThePayload()
    {
        var snapshot = await this.Builder(this.Resolver(this.StateAt(Now), currentMonthAuthors: null)).BuildAsync(
            UsageStatisticsTestDoubles.EnabledState(Now),
            UsageStatisticsEdition.Commercial);

        Assert.Null(snapshot.ConsumedAuthorsPerMonth);
        Assert.Equal(12, snapshot.ConsumedClients);
        Assert.DoesNotContain(
            "consumedAuthorsPerMonth",
            UsageStatisticsContract.Serialize(snapshot),
            StringComparison.Ordinal);
    }

    // A month the installation does measure and that holds no counted author reports the zero it measured.
    // Only an installation that cannot measure the dimension leaves the field out.
    [Fact]
    public async Task AMonthThatHoldsNoCountedAuthor_ReportsZeroRatherThanLeavingTheFieldOut()
    {
        var snapshot = await this.Builder(this.Resolver(this.StateAt(Now), currentMonthAuthors: 0)).BuildAsync(
            UsageStatisticsTestDoubles.EnabledState(Now),
            UsageStatisticsEdition.Commercial);

        Assert.Equal(0, snapshot.ConsumedAuthorsPerMonth);
        Assert.Contains(
            "\"consumedAuthorsPerMonth\":0",
            UsageStatisticsContract.Serialize(snapshot),
            StringComparison.Ordinal);
    }

    // Without the licensing module there is no license state to read, which is the same position as an
    // installation whose license is not in force.
    [Fact]
    public async Task AnInstallationWithoutTheLicensingModule_ReportsNothing()
    {
        var resolver = new UsageStatisticsLicensedConsumptionResolver(NullLogger<UsageStatisticsLicensedConsumptionResolver>.Instance);

        Assert.Null(await resolver.ResolveAsync());
    }

    // The licensing dependencies are optional constructor parameters, so the container has to be able to
    // construct the resolver with none of them registered. A failure here surfaces as a send cycle that throws
    // rather than as a missing field.
    [Fact]
    public void TheResolver_IsConstructableWithoutAnyLicensingRegistration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<UsageStatisticsLicensedConsumptionResolver>();

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<UsageStatisticsLicensedConsumptionResolver>());
    }

    // The reads are skipped rather than made and discarded, so a community installation makes no consumption
    // read for a ping that would not carry the values.
    [Fact]
    public async Task ACommunityCycle_MakesNoConsumptionRead()
    {
        var stateProvider = UsageStatisticsTestDoubles.LicenseStateProvider(this.StateAt(Now));
        var countSource = UsageStatisticsTestDoubles.LicensedResourceCountSource(Counts);
        var rollupStore = AuthorActivityRollupStore(CurrentMonthAuthors)!;

        var resolver = new UsageStatisticsLicensedConsumptionResolver(
            NullLogger<UsageStatisticsLicensedConsumptionResolver>.Instance,
            stateProvider,
            UsageStatisticsTestDoubles.LicensingIdentityStore(LicensingIdentity),
            UsageStatisticsTestDoubles.SystemProfileStore(ProfileHash),
            countSource,
            authorActivityRollupStore: rollupStore);

        await this.Builder(resolver).BuildAsync(
            UsageStatisticsTestDoubles.EnabledState(Now),
            UsageStatisticsEdition.Community);

        await stateProvider.DidNotReceiveWithAnyArgs().GetStateAsync(default);
        await countSource.DidNotReceiveWithAnyArgs().GetCountsAsync(default);
        await rollupStore.DidNotReceiveWithAnyArgs().GetCurrentMonthCountsAsync(default);
    }

    private async Task<UsageStatisticsSnapshot> BuildAsync(LicenseState licenseState)
    {
        return await this.Builder(this.Resolver(licenseState)).BuildAsync(
            UsageStatisticsTestDoubles.EnabledState(Now),
            UsageStatisticsEdition.Commercial);
    }

    private UsageStatisticsSnapshotBuilder Builder(UsageStatisticsLicensedConsumptionResolver resolver)
    {
        return new UsageStatisticsSnapshotBuilder(
            UsageStatisticsTestDoubles.CountSource(ActivityCounts),
            UsageStatisticsTestDoubles.ProductVersion("1.2.3"),
            new FakeTimeProvider(Now),
            resolver);
    }

    private UsageStatisticsLicensedConsumptionResolver Resolver(
        LicenseState licenseState,
        string? profileHash = ProfileHash,
        long? previousDayPeak = PreviousDayPeak,
        long? currentMonthAuthors = CurrentMonthAuthors)
    {
        return new UsageStatisticsLicensedConsumptionResolver(
            NullLogger<UsageStatisticsLicensedConsumptionResolver>.Instance,
            UsageStatisticsTestDoubles.LicenseStateProvider(licenseState),
            UsageStatisticsTestDoubles.LicensingIdentityStore(LicensingIdentity),
            UsageStatisticsTestDoubles.SystemProfileStore(profileHash),
            UsageStatisticsTestDoubles.LicensedResourceCountSource(Counts),
            ConcurrentReviewPeakStore(previousDayPeak),
            AuthorActivityRollupStore(currentMonthAuthors));
    }

    /// <summary>A peak store answering with the count the previous UTC day reached, or with none.</summary>
    private static IConcurrentReviewPeakStore ConcurrentReviewPeakStore(long? previousDayPeak)
    {
        var store = Substitute.For<IConcurrentReviewPeakStore>();
        store.GetPreviousDayPeakAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(previousDayPeak));

        return store;
    }

    /// <summary>
    ///     A rollup answering with the counted authors of the current month, or no rollup at all when the
    ///     installation has none to read.
    /// </summary>
    /// <remarks>
    ///     The excluded authors it reports differ from the counted ones, so a report that carried the excluded
    ///     number instead of the counted one fails.
    /// </remarks>
    private static IAuthorActivityRollupStore? AuthorActivityRollupStore(long? countedAuthors)
    {
        if (countedAuthors is not { } counted)
        {
            return null;
        }

        var store = Substitute.For<IAuthorActivityRollupStore>();
        store.GetCurrentMonthCountsAsync(Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(
                    new AuthorActivityMonthCounts(
                        new DateOnly(Now.Year, Now.Month, 1),
                        counted,
                        CurrentMonthExcludedAuthors)));

        return store;
    }

    /// <summary>A resolver whose named read throws and whose other reads answer as they would.</summary>
    private UsageStatisticsLicensedConsumptionResolver ResolverWithFailingRead(string failingRead)
    {
        var stateProvider = UsageStatisticsTestDoubles.LicenseStateProvider(this.StateAt(Now));
        var identityStore = UsageStatisticsTestDoubles.LicensingIdentityStore(LicensingIdentity);
        var profileStore = UsageStatisticsTestDoubles.SystemProfileStore(ProfileHash);
        var countSource = UsageStatisticsTestDoubles.LicensedResourceCountSource(Counts);
        var peakStore = ConcurrentReviewPeakStore(PreviousDayPeak);
        var rollupStore = AuthorActivityRollupStore(CurrentMonthAuthors)!;

        switch (failingRead)
        {
            case nameof(ILicenseStateProvider):
                stateProvider.GetStateAsync(Arg.Any<CancellationToken>())
                    .Returns<Task<LicenseState>>(_ => throw new InvalidOperationException("read failed"));
                break;
            case nameof(ILicensingIdentityStore):
                identityStore.GetOrCreateAsync(Arg.Any<CancellationToken>())
                    .Returns<Task<Guid>>(_ => throw new InvalidOperationException("read failed"));
                break;
            case nameof(ISystemProfileStore):
                profileStore.GetCurrentAsync(Arg.Any<CancellationToken>())
                    .Returns<Task<SystemProfileSnapshot?>>(_ => throw new InvalidOperationException("read failed"));
                break;
            case nameof(IConcurrentReviewPeakStore):
                peakStore.GetPreviousDayPeakAsync(Arg.Any<CancellationToken>())
                    .Returns<Task<long?>>(_ => throw new InvalidOperationException("read failed"));
                break;
            case nameof(IAuthorActivityRollupStore):
                rollupStore.GetCurrentMonthCountsAsync(Arg.Any<CancellationToken>())
                    .Returns<Task<AuthorActivityMonthCounts>>(_ => throw new InvalidOperationException("read failed"));
                break;
            default:
                countSource.GetCountsAsync(Arg.Any<CancellationToken>())
                    .Returns<Task<LicensedResourceCounts>>(_ => throw new InvalidOperationException("read failed"));
                break;
        }

        return new UsageStatisticsLicensedConsumptionResolver(
            NullLogger<UsageStatisticsLicensedConsumptionResolver>.Instance,
            stateProvider,
            identityStore,
            profileStore,
            countSource,
            peakStore,
            rollupStore);
    }

    /// <summary>
    ///     A license state over a document this test's chain signed and the product's own verifier accepted, so
    ///     the identifier that reaches the wire is the one a stored document carries.
    /// </summary>
    private LicenseState StateAt(DateTimeOffset evaluatedAt)
    {
        var compactLicense = this._chain.Sign(LicenseTestChain.ClaimsFor(NotBefore, ExpiresAt));
        var verification = this._verifier.Verify(compactLicense, evaluatedAt);
        Assert.True(verification.IsVerified, verification.FailureDetail);

        return LicenseState.Verified(verification.License, evaluatedAt, NotBefore, null);
    }
}

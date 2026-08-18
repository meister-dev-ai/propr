// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.UsageStatistics.Models;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.UsageStatistics.Persistence;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.UsageStatistics;

public sealed class UsageStatisticsStateRepositoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    // A fresh installation starts with the gate shut and the toggle on: an installation nobody has
    // administered sends nothing, and one that has been administered sends without an operator turning it on.
    [Fact]
    public async Task AFreshInstallation_StartsWithTheGateShutAndTheToggleOn()
    {
        await using var db = CreateContext();
        var sut = new UsageStatisticsStateRepository(db, new FakeTimeProvider(Now));

        var state = await sut.GetAsync();

        Assert.True(state.CommunityOptIn);
        Assert.False(state.IsConsentGateSatisfied);
        Assert.False(state.IsSendingEnabled(UsageStatisticsEdition.Community));
        Assert.False(state.IsSendingEnabled(UsageStatisticsEdition.Commercial));
        Assert.NotEqual(Guid.Empty, state.InstanceId);
    }

    // The identifier is the only key that links one installation's reports over time, so it has to survive
    // every later read.
    [Fact]
    public async Task TheInstallationIdentity_IsCreatedOnceAndKept()
    {
        await using var db = CreateContext();
        var sut = new UsageStatisticsStateRepository(db, new FakeTimeProvider(Now));

        var first = await sut.GetAsync();
        var second = await sut.GetAsync();
        await sut.SetCommunityOptInAsync(false, null);
        var third = await sut.GetAsync();

        Assert.Equal(first.InstanceId, second.InstanceId);
        Assert.Equal(first.InstanceId, third.InstanceId);
        Assert.Equal(1, await db.UsageStatisticsIdentity.CountAsync());
    }

    [Fact]
    public async Task TurningItOff_TakesEffectImmediatelyAndRecordsWhoDidIt()
    {
        var actor = Guid.NewGuid();
        await using var db = CreateContext();
        var sut = new UsageStatisticsStateRepository(db, new FakeTimeProvider(Now));

        var state = await sut.SetCommunityOptInAsync(false, actor);

        Assert.False(state.CommunityOptIn);
        var stored = await db.UsageStatisticsSettings.AsNoTracking().SingleAsync();
        Assert.Equal(actor, stored.UpdatedByUserId);
        Assert.Equal(Now, stored.UpdatedAt);
    }

    [Fact]
    public async Task OpeningTheGate_IsIdempotentAndKeepsTheFirstTimestamp()
    {
        await using var db = CreateContext();
        var timeProvider = new FakeTimeProvider(Now);
        var sut = new UsageStatisticsStateRepository(db, timeProvider);

        var first = await sut.RecordConsentGateSatisfiedAsync();
        timeProvider.Advance(TimeSpan.FromDays(2));
        var second = await sut.RecordConsentGateSatisfiedAsync();

        Assert.Equal(Now, first.ConsentGateSatisfiedAt);
        Assert.Equal(Now, second.ConsentGateSatisfiedAt);
    }

    // Dismissing hides the notice. It is not a second opt-out, so what the installation sends does not change.
    [Fact]
    public async Task DismissingTheNotice_DoesNotChangeWhatIsSent()
    {
        await using var db = CreateContext();
        var sut = new UsageStatisticsStateRepository(db, new FakeTimeProvider(Now));
        await sut.RecordConsentGateSatisfiedAsync();

        var state = await sut.RecordNoticeDismissedAsync();

        Assert.Equal(Now, state.NoticeDismissedAt);
        Assert.True(state.IsSendingEnabled(UsageStatisticsEdition.Community));
    }

    [Fact]
    public async Task ASuccessfulSend_MovesTheWindowForwardAndStoresWhatTheReceiverSaid()
    {
        await using var db = CreateContext();
        var sut = new UsageStatisticsStateRepository(db, new FakeTimeProvider(Now));

        var response = new UsageStatisticsPingResponse
        {
            LatestVersion = "1.0.0.alpha.0050",
            Advisories =
            [
                new ProductAdvisory
                {
                    Id = "PROPR-2026-0001",
                    Severity = "high",
                    Title = "A thing",
                    AffectedVersions = "< 1.0.0.alpha.0050",
                    Link = "https://example.invalid/advisory",
                },
            ],
        };

        var state = await sut.RecordSendOutcomeAsync(new UsageStatisticsSendOutcome(Now, true, "Delivered.", response));

        Assert.Equal(Now, state.LastAttemptAt);
        Assert.Equal(Now, state.LastSuccessAt);
        Assert.True(state.LastAttemptSucceeded);
        Assert.Equal("1.0.0.alpha.0050", state.LatestVersion);
        var advisory = Assert.Single(state.Advisories);
        Assert.Equal("PROPR-2026-0001", advisory.Id);
        Assert.Equal("high", advisory.Severity);
    }

    // A failed send still counts as an attempt, which keeps an unreachable receiver from being retried
    // continuously. It must not move the observation window, because nothing was delivered.
    [Fact]
    public async Task AFailedSend_CountsAsAnAttemptWithoutMovingTheWindow()
    {
        await using var db = CreateContext();
        var sut = new UsageStatisticsStateRepository(db, new FakeTimeProvider(Now));

        var state = await sut.RecordSendOutcomeAsync(new UsageStatisticsSendOutcome(Now, false, "The receiver could not be reached.", null));

        Assert.Equal(Now, state.LastAttemptAt);
        Assert.Null(state.LastSuccessAt);
        Assert.False(state.LastAttemptSucceeded);
    }

    // A response that carried no update information leaves the previously reported version in place.
    [Fact]
    public async Task AnEmptyAnswer_LeavesTheLastKnownUpdateInformationInPlace()
    {
        await using var db = CreateContext();
        var sut = new UsageStatisticsStateRepository(db, new FakeTimeProvider(Now));

        await sut.RecordSendOutcomeAsync(
            new UsageStatisticsSendOutcome(
                Now,
                true,
                "Delivered.",
                new UsageStatisticsPingResponse { LatestVersion = "1.0.0.alpha.0050" }));

        var state = await sut.RecordSendOutcomeAsync(
            new UsageStatisticsSendOutcome(
                Now.AddDays(1),
                true,
                "Delivered.",
                new UsageStatisticsPingResponse()));

        Assert.Equal("1.0.0.alpha.0050", state.LatestVersion);
    }

    // The claim stops two replicas that woke together from both sending. It moves the timestamp, so the
    // second caller finds the day already claimed.
    [Fact]
    public async Task ClaimingTheDay_SucceedsOnceAndThenRefuses()
    {
        await using var db = CreateContext();
        var sut = new UsageStatisticsStateRepository(db, new FakeTimeProvider(Now));
        var notBefore = Now.AddHours(-20);

        Assert.True(await sut.TryClaimSendAsync(notBefore, Now));
        Assert.False(await sut.TryClaimSendAsync(notBefore, Now));

        var state = await sut.GetAsync();
        Assert.Equal(Now, state.LastAttemptAt);
    }

    [Fact]
    public async Task ClaimingTheDay_SucceedsAgainOnceTheIntervalHasPassed()
    {
        await using var db = CreateContext();
        var timeProvider = new FakeTimeProvider(Now);
        var sut = new UsageStatisticsStateRepository(db, timeProvider);

        Assert.True(await sut.TryClaimSendAsync(Now.AddHours(-20), Now));

        var tomorrow = Now.AddDays(1);
        Assert.True(await sut.TryClaimSendAsync(tomorrow.AddHours(-20), tomorrow));
    }

    // The observation window is measured from the last successful delivery. A claim is not a delivery, so it
    // must leave that timestamp unchanged.
    [Fact]
    public async Task ClaimingTheDay_DoesNotMoveTheObservationWindow()
    {
        await using var db = CreateContext();
        var sut = new UsageStatisticsStateRepository(db, new FakeTimeProvider(Now));
        await sut.RecordSendOutcomeAsync(new UsageStatisticsSendOutcome(Now.AddDays(-3), true, "Delivered.", null));

        await sut.TryClaimSendAsync(Now.AddHours(-20), Now);

        var state = await sut.GetAsync();
        Assert.Equal(Now.AddDays(-3), state.LastSuccessAt);
    }

    // Dropping the whole list would clear advisories already shown to the operator.
    [Fact]
    public async Task AnOverlongAdvisoryList_KeepsAsManyEntriesAsFit()
    {
        await using var db = CreateContext();
        var sut = new UsageStatisticsStateRepository(db, new FakeTimeProvider(Now));

        var advisories = Enumerable.Range(0, 400)
            .Select(index => new ProductAdvisory
            {
                Id = $"PROPR-2026-{index:D4}",
                Severity = "high",
                Title = new string('t', 200),
                Link = "https://example.invalid/advisory",
            })
            .ToList();

        var state = await sut.RecordSendOutcomeAsync(
            new UsageStatisticsSendOutcome(
                Now,
                true,
                "Delivered.",
                new UsageStatisticsPingResponse { Advisories = advisories }));

        Assert.NotEmpty(state.Advisories);
        Assert.True(state.Advisories.Count < advisories.Count);
        Assert.Equal("PROPR-2026-0000", state.Advisories[0].Id);
    }

    [Fact]
    public async Task AnOverlongOutcomeDescription_IsTruncatedRatherThanRejected()
    {
        await using var db = CreateContext();
        var sut = new UsageStatisticsStateRepository(db, new FakeTimeProvider(Now));

        var state = await sut.RecordSendOutcomeAsync(new UsageStatisticsSendOutcome(Now, false, new string('x', 4000), null));

        Assert.Equal(256, state.LastAttemptDetail?.Length);
    }

    // A send that failed does not hold the interval. The claim moves the timestamp before the request is made,
    // so without this a receiver that answered 502 once cost a whole day, recoverable only by an UPDATE against
    // the installation's own database.
    [Fact]
    public async Task AFailedAttempt_CanBeClaimedAgainWithoutWaiting()
    {
        await using var db = CreateContext();
        var sut = new UsageStatisticsStateRepository(db, new FakeTimeProvider(Now));
        var notBefore = Now.AddHours(-20);

        Assert.True(await sut.TryClaimSendAsync(notBefore, Now));
        await sut.RecordSendOutcomeAsync(new UsageStatisticsSendOutcome(Now, false, "The receiver answered 502.", null));

        Assert.True(await sut.TryClaimSendAsync(notBefore, Now.AddMinutes(1)));
    }

    // A send that succeeded still holds it, so a success is not retried within the interval.
    [Fact]
    public async Task ASuccessfulAttempt_StillHoldsTheInterval()
    {
        await using var db = CreateContext();
        var sut = new UsageStatisticsStateRepository(db, new FakeTimeProvider(Now));
        var notBefore = Now.AddHours(-20);

        Assert.True(await sut.TryClaimSendAsync(notBefore, Now));
        await sut.RecordSendOutcomeAsync(new UsageStatisticsSendOutcome(Now, true, "Delivered.", null));

        Assert.False(await sut.TryClaimSendAsync(notBefore, Now.AddMinutes(1)));
    }

    // The claim clears the previous verdict. Otherwise a claim that never reached the outcome write left the
    // earlier "Delivered." standing against the new timestamp, so the settings page asserted a delivery that
    // never happened.
    [Fact]
    public async Task ClaimingTheDay_ClearsThePreviousVerdict()
    {
        await using var db = CreateContext();
        var sut = new UsageStatisticsStateRepository(db, new FakeTimeProvider(Now));

        Assert.True(await sut.TryClaimSendAsync(Now.AddHours(-20), Now));
        await sut.RecordSendOutcomeAsync(new UsageStatisticsSendOutcome(Now, true, "Delivered.", null));

        Assert.True(await sut.TryClaimSendAsync(Now.AddHours(4), Now.AddHours(24)));

        var state = await sut.GetAsync();
        Assert.Null(state.LastAttemptSucceeded);
        Assert.Null(state.LastAttemptDetail);
        Assert.Equal(Now.AddHours(24), state.LastAttemptAt);
    }

    private static MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"TestDb_UsageStatisticsState_{Guid.NewGuid()}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new MeisterProPRDbContext(options);
    }
}

[Collection("PostgresIntegration")]
public sealed class UsageStatisticsStateRepositoryPostgresTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();
        return this.ResetTablesAsync();
    }

    public Task DisposeAsync()
    {
        return this.ResetTablesAsync();
    }

    // Two replicas starting together each propose an identifier. Two identities would report one installation
    // as two.
    [Fact]
    public async Task ConcurrentFirstReads_ProduceOneIdentity()
    {
        await using var first = this.CreatePostgresContext();
        await using var second = this.CreatePostgresContext();

        var states = await Task.WhenAll(
            new UsageStatisticsStateRepository(first, new FakeTimeProvider(Now)).GetAsync(),
            new UsageStatisticsStateRepository(second, new FakeTimeProvider(Now)).GetAsync());

        Assert.Equal(states[0].InstanceId, states[1].InstanceId);

        await using var verification = this.CreatePostgresContext();
        Assert.Equal(1, await verification.UsageStatisticsIdentity.CountAsync());
        Assert.Equal(1, await verification.UsageStatisticsSettings.CountAsync());
    }

    // The row exists to hold the identifier, so a restart must not generate a new one.
    [Fact]
    public async Task TheIdentity_SurvivesANewConnection()
    {
        await using var first = this.CreatePostgresContext();
        var original = await new UsageStatisticsStateRepository(first, new FakeTimeProvider(Now)).GetAsync();

        await using var second = this.CreatePostgresContext();
        var reread = await new UsageStatisticsStateRepository(second, new FakeTimeProvider(Now)).GetAsync();

        Assert.Equal(original.InstanceId, reread.InstanceId);
    }

    // The production path is the conditional UPDATE, not the read-modify-write the in-memory provider takes, so
    // the same two rules are asserted against PostgreSQL.
    [Fact]
    public async Task AFailedAttempt_CanBeClaimedAgainWithoutWaiting()
    {
        await using var db = this.CreatePostgresContext();
        var sut = new UsageStatisticsStateRepository(db, new FakeTimeProvider(Now));
        var notBefore = Now.AddHours(-20);

        Assert.True(await sut.TryClaimSendAsync(notBefore, Now));
        await sut.RecordSendOutcomeAsync(new UsageStatisticsSendOutcome(Now, false, "The receiver answered 502.", null));

        Assert.True(await sut.TryClaimSendAsync(notBefore, Now.AddMinutes(1)));
    }

    [Fact]
    public async Task ASuccessfulAttempt_StillHoldsTheInterval()
    {
        await using var db = this.CreatePostgresContext();
        var sut = new UsageStatisticsStateRepository(db, new FakeTimeProvider(Now));
        var notBefore = Now.AddHours(-20);

        Assert.True(await sut.TryClaimSendAsync(notBefore, Now));
        await sut.RecordSendOutcomeAsync(new UsageStatisticsSendOutcome(Now, true, "Delivered.", null));

        Assert.False(await sut.TryClaimSendAsync(notBefore, Now.AddMinutes(1)));
    }

    // Two replicas must not both claim a day whose last attempt failed, which is why the claim clears the
    // verdict rather than leaving FALSE for the next caller to match on.
    [Fact]
    public async Task TwoReplicasAfterAFailedAttempt_ProduceOneClaim()
    {
        await using var seed = this.CreatePostgresContext();
        var seeding = new UsageStatisticsStateRepository(seed, new FakeTimeProvider(Now));
        Assert.True(await seeding.TryClaimSendAsync(Now.AddHours(-20), Now));
        await seeding.RecordSendOutcomeAsync(new UsageStatisticsSendOutcome(Now, false, "The receiver answered 502.", null));

        await using var first = this.CreatePostgresContext();
        await using var second = this.CreatePostgresContext();

        var claims = await Task.WhenAll(
            new UsageStatisticsStateRepository(first, new FakeTimeProvider(Now)).TryClaimSendAsync(Now.AddHours(-20), Now.AddMinutes(1)),
            new UsageStatisticsStateRepository(second, new FakeTimeProvider(Now)).TryClaimSendAsync(Now.AddHours(-20), Now.AddMinutes(1)));

        Assert.Single(claims, claimed => claimed);
    }

    private async Task ResetTablesAsync()
    {
        await using var db = this.CreatePostgresContext();
        await db.UsageStatisticsSettings.ExecuteDeleteAsync();
        await db.UsageStatisticsIdentity.ExecuteDeleteAsync();
    }

    private MeisterProPRDbContext CreatePostgresContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UseVector())
            .Options;

        return new MeisterProPRDbContext(options);
    }
}

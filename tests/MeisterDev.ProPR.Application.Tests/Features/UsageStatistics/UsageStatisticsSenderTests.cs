// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Models;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Ports;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Services;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.UsageStatistics;

public sealed class UsageStatisticsSenderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AnInstallationWithTheToggleOff_DoesNotSend()
    {
        var state = UsageStatisticsTestDoubles.EnabledState(Now) with { CommunityOptIn = false };

        Assert.Equal(
            UsageStatisticsSendDecision.Disabled,
            UsageStatisticsSender.Decide(state, UsageStatisticsEdition.Community, Now));
    }

    // A license activates sending even when the toggle was switched off before the license was installed. The
    // stored preference is left unchanged, so removing the license returns control at its stored value.
    [Fact]
    public void AnInstallationWithALicense_SendsDespiteTheStoredCommunityPreference()
    {
        var state = UsageStatisticsTestDoubles.EnabledState(Now) with { CommunityOptIn = false };

        Assert.Equal(
            UsageStatisticsSendDecision.Sent,
            UsageStatisticsSender.Decide(state, UsageStatisticsEdition.Commercial, Now));
    }

    // The gate fails closed. An installation no administrator has reached sends nothing.
    [Fact]
    public void AnInstallationWithNoAdministratorSignIn_DoesNotSend()
    {
        var state = UsageStatisticsTestDoubles.EnabledState(Now) with { ConsentGateSatisfiedAt = null };

        Assert.Equal(
            UsageStatisticsSendDecision.AwaitingConsent,
            UsageStatisticsSender.Decide(state, UsageStatisticsEdition.Community, Now));
    }

    [Fact]
    public void AnInstallationWithALicenseButNoAdministratorYet_StillDoesNotSend()
    {
        var state = UsageStatisticsTestDoubles.EnabledState(Now) with { ConsentGateSatisfiedAt = null };

        Assert.Equal(
            UsageStatisticsSendDecision.AwaitingConsent,
            UsageStatisticsSender.Decide(state, UsageStatisticsEdition.Commercial, Now));
    }

    // One snapshot a day. A host that restarts every few hours must not send one per restart.
    [Fact]
    public void AnInstallationThatSentRecently_WaitsForTheNextDay()
    {
        var state = UsageStatisticsTestDoubles.EnabledState(Now) with { LastAttemptAt = Now.AddHours(-2) };

        Assert.Equal(
            UsageStatisticsSendDecision.NotDue,
            UsageStatisticsSender.Decide(state, UsageStatisticsEdition.Community, Now));
    }

    // A timestamp ahead of this replica's clock is treated as not due. Reading it as due would let clock skew
    // between two replicas produce a second snapshot each day.
    [Fact]
    public void AnAttemptTimestampFromTheFuture_IsTreatedAsNotDue()
    {
        var state = UsageStatisticsTestDoubles.EnabledState(Now) with { LastAttemptAt = Now.AddMinutes(4) };

        Assert.Equal(
            UsageStatisticsSendDecision.NotDue,
            UsageStatisticsSender.Decide(state, UsageStatisticsEdition.Community, Now));
    }

    // A failed attempt does not hold the interval: the claim moved the timestamp before the request was made,
    // and a snapshot that did not arrive cannot be a duplicate.
    [Fact]
    public void AFailedAttemptInsideTheInterval_IsStillDue()
    {
        var state = UsageStatisticsTestDoubles.EnabledState(Now) with
        {
            LastAttemptAt = Now.AddMinutes(-5),
            LastAttemptSucceeded = false,
        };

        Assert.Equal(
            UsageStatisticsSendDecision.Sent,
            UsageStatisticsSender.Decide(state, UsageStatisticsEdition.Community, Now));
    }

    // A successful attempt inside the interval still holds it.
    [Fact]
    public void ASuccessfulAttemptInsideTheInterval_IsNotDue()
    {
        var state = UsageStatisticsTestDoubles.EnabledState(Now) with
        {
            LastAttemptAt = Now.AddMinutes(-5),
            LastAttemptSucceeded = true,
        };

        Assert.Equal(
            UsageStatisticsSendDecision.NotDue,
            UsageStatisticsSender.Decide(state, UsageStatisticsEdition.Community, Now));
    }

    [Fact]
    public void AnInstallationThatLastSentYesterday_IsDue()
    {
        var state = UsageStatisticsTestDoubles.EnabledState(Now) with { LastAttemptAt = Now.AddHours(-25) };

        Assert.Equal(
            UsageStatisticsSendDecision.Sent,
            UsageStatisticsSender.Decide(state, UsageStatisticsEdition.Community, Now));
    }

    [Fact]
    public async Task ADueCycle_DeliversASnapshotAndRecordsTheOutcome()
    {
        var store = CreateStore(UsageStatisticsTestDoubles.EnabledState(Now));
        var pingClient = Substitute.For<IUsageStatisticsPingClient>();
        pingClient.SendAsync(Arg.Any<UsageStatisticsSnapshot>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new UsageStatisticsSendOutcome(Now, true, "Delivered.", null)));

        var result = await CreateSender(store, pingClient).SendIfDueAsync();

        Assert.Equal(UsageStatisticsSendDecision.Sent, result.Decision);
        await pingClient.Received(1).SendAsync(Arg.Any<UsageStatisticsSnapshot>(), Arg.Any<CancellationToken>());
        await store.Received(1).RecordSendOutcomeAsync(
            Arg.Is<UsageStatisticsSendOutcome>(outcome => outcome.Succeeded),
            Arg.Any<CancellationToken>());
    }

    // The claim moves the attempt timestamp before the request goes out, so a process that dies mid-send does
    // not send again on its next start.
    [Fact]
    public async Task ADueCycle_ClaimsTheDayBeforeItSends()
    {
        var store = CreateStore(UsageStatisticsTestDoubles.EnabledState(Now));
        var pingClient = Substitute.For<IUsageStatisticsPingClient>();
        pingClient.SendAsync(Arg.Any<UsageStatisticsSnapshot>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new UsageStatisticsSendOutcome(Now, true, "Delivered.", null)));

        await CreateSender(store, pingClient).SendIfDueAsync();

        Received.InOrder(() =>
        {
            store.TryClaimSendAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
            pingClient.SendAsync(Arg.Any<UsageStatisticsSnapshot>(), Arg.Any<CancellationToken>());
        });
    }

    // Two replicas that wake together both read a due state. Only the one that wins the conditional update may
    // send; the other must not produce a second snapshot for the same day.
    [Fact]
    public async Task AReplicaThatLosesTheClaim_SendsNothing()
    {
        var store = CreateStore(UsageStatisticsTestDoubles.EnabledState(Now), claimSucceeds: false);
        var pingClient = Substitute.For<IUsageStatisticsPingClient>();

        var result = await CreateSender(store, pingClient).SendIfDueAsync();

        Assert.Equal(UsageStatisticsSendDecision.NotDue, result.Decision);
        await pingClient.DidNotReceiveWithAnyArgs().SendAsync(null!, default);
    }

    // The snapshot has already been sent by then. Treating a failed write as an unspent day would send another
    // snapshot on the next cycle.
    [Fact]
    public async Task AFailureToStoreTheOutcome_DoesNotCauseASecondSend()
    {
        var store = CreateStore(UsageStatisticsTestDoubles.EnabledState(Now));
        store.RecordSendOutcomeAsync(Arg.Any<UsageStatisticsSendOutcome>(), Arg.Any<CancellationToken>())
            .Returns<Task<UsageStatisticsState>>(_ => throw new InvalidOperationException("write failed"));

        var pingClient = Substitute.For<IUsageStatisticsPingClient>();
        pingClient.SendAsync(Arg.Any<UsageStatisticsSnapshot>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new UsageStatisticsSendOutcome(Now, true, "Delivered.", null)));

        var result = await CreateSender(store, pingClient).SendIfDueAsync();

        Assert.Equal(UsageStatisticsSendDecision.Sent, result.Decision);
        Assert.Equal(Now, result.LastAttemptAt);
    }

    // An inert cycle never reaches the transport, so the off state performs no network activity.
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task AnInertCycle_NeverReachesTheTransport(bool optIn, bool consentGiven)
    {
        var state = UsageStatisticsTestDoubles.EnabledState(Now) with
        {
            CommunityOptIn = optIn,
            ConsentGateSatisfiedAt = consentGiven ? Now.AddDays(-1) : null,
        };

        var store = CreateStore(state);
        var pingClient = Substitute.For<IUsageStatisticsPingClient>();

        var result = await CreateSender(store, pingClient).SendIfDueAsync();

        Assert.NotEqual(UsageStatisticsSendDecision.Sent, result.Decision);
        await pingClient.DidNotReceiveWithAnyArgs().SendAsync(null!, default);
        await store.DidNotReceiveWithAnyArgs().RecordSendOutcomeAsync(null!, default);
        await store.DidNotReceiveWithAnyArgs().TryClaimSendAsync(default, default, default);
    }

    // A failed send still counts as an attempt. Recording it stops an unreachable receiver from being retried
    // on every loop iteration.
    [Fact]
    public async Task AFailedSend_IsRecordedWithoutRetrying()
    {
        var store = CreateStore(UsageStatisticsTestDoubles.EnabledState(Now));
        var pingClient = Substitute.For<IUsageStatisticsPingClient>();
        pingClient.SendAsync(Arg.Any<UsageStatisticsSnapshot>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new UsageStatisticsSendOutcome(Now, false, "The receiver could not be reached.", null)));

        await CreateSender(store, pingClient).SendIfDueAsync();

        await pingClient.Received(1).SendAsync(Arg.Any<UsageStatisticsSnapshot>(), Arg.Any<CancellationToken>());
        await store.Received(1).RecordSendOutcomeAsync(
            Arg.Is<UsageStatisticsSendOutcome>(outcome => !outcome.Succeeded),
            Arg.Any<CancellationToken>());
    }

    private static IUsageStatisticsStateStore CreateStore(UsageStatisticsState state, bool claimSucceeds = true)
    {
        var store = Substitute.For<IUsageStatisticsStateStore>();
        store.GetAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(state));
        store.TryClaimSendAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(claimSucceeds));
        store.RecordSendOutcomeAsync(Arg.Any<UsageStatisticsSendOutcome>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(state));

        return store;
    }

    private static UsageStatisticsSender CreateSender(
        IUsageStatisticsStateStore store,
        IUsageStatisticsPingClient pingClient)
    {
        var timeProvider = new FakeTimeProvider(Now);
        var builder = new UsageStatisticsSnapshotBuilder(
            UsageStatisticsTestDoubles.CountSource(new UsageStatisticsCounts(1, 0, 0, null, null)),
            UsageStatisticsTestDoubles.ProductVersion("1.2.3"),
            timeProvider);

        return new UsageStatisticsSender(
            store,
            builder,
            UsageStatisticsTestDoubles.EditionResolver(InstallationEdition.Community),
            pingClient,
            timeProvider);
    }
}

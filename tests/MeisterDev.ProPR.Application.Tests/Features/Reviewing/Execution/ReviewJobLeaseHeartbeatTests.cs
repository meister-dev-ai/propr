// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Execution;

public sealed class ReviewJobLeaseHeartbeatTests
{
    private static readonly ReviewJobLease Lease =
        new(Guid.NewGuid(), "host-a:1234", 7, DateTimeOffset.UtcNow.AddMinutes(2));

    private static ReviewLeaseOptions Options(int maxFailures = 3)
    {
        return new ReviewLeaseOptions
        {
            LeaseDurationSeconds = 120,
            HeartbeatIntervalSeconds = 20,
            // No jitter, so the advance a test makes lands exactly on a renewal instead of just short of one.
            HeartbeatJitterFraction = 0,
            MaxConsecutiveHeartbeatFailures = maxFailures,
        };
    }

    private static ReviewJobLeaseHeartbeat Start(
        IReviewJobLeaseStore store,
        FakeTimeProvider time,
        ReviewLeaseOptions? options = null)
    {
        return ReviewJobLeaseHeartbeat.Start(
            Lease,
            store,
            options ?? Options(),
            time,
            NullLogger.Instance);
    }

    /// <summary>
    ///     Nudges the fake clock forward until the condition holds. The renewal loop registers its timer on a
    ///     background task, so a single advance can land before there is any timer to fire; repeating it
    ///     keeps the test about what the heartbeat does rather than about when its first delay was scheduled.
    /// </summary>
    private static async Task AdvanceUntilAsync(FakeTimeProvider time, Func<bool> condition, string failureMessage)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            time.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(5, CancellationToken.None);
        }

        Assert.Fail(failureMessage);
    }

    // Renewal has to run on its own schedule. If it only happened where the pipeline chose to report
    // progress, one long AI call would let a perfectly healthy lease lapse.
    [Fact]
    public async Task Renews_OnTheConfiguredInterval_WithoutAnyPipelineProgress()
    {
        var store = Substitute.For<IReviewJobLeaseStore>();
        store.TryRenewAsync(Arg.Any<ReviewJobLease>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewJobLeaseRenewal(true, DateTimeOffset.UtcNow.AddMinutes(2)));
        var time = new FakeTimeProvider();

        await using var heartbeat = Start(store, time);

        await AdvanceUntilAsync(
            time,
            () => store.ReceivedCalls().Any(),
            "The heartbeat never renewed the lease.");

        await AdvanceUntilAsync(
            time,
            () => store.ReceivedCalls().Count() >= 2,
            "The heartbeat renewed once and then stopped.");

        Assert.False(heartbeat.IsLeaseLost);
    }

    // A rejection means somebody else holds the job now. Retrying cannot win it back, so the holder has to
    // stop immediately rather than keep reviewing a job it no longer owns.
    [Fact]
    public async Task SignalsLeaseLost_AsSoonAsARenewalIsRejected()
    {
        var store = Substitute.For<IReviewJobLeaseStore>();
        store.TryRenewAsync(Arg.Any<ReviewJobLease>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(ReviewJobLeaseRenewal.Rejected);
        var time = new FakeTimeProvider();

        await using var heartbeat = Start(store, time);

        await AdvanceUntilAsync(
            time,
            () => heartbeat.LeaseLost.IsCancellationRequested,
            "A rejected renewal did not signal that the lease was lost.");
        Assert.True(heartbeat.IsLeaseLost);
    }

    // A transient database blip is not the same as losing the job, so renewal retries. It gives up only
    // after the configured number of consecutive failures, because by then the lease is about to expire and
    // continuing would risk two hosts reviewing the same job.
    [Fact]
    public async Task ToleratesTransientFailures_ThenGivesUpAtTheConfiguredLimit()
    {
        var attempts = 0;
        var store = Substitute.For<IReviewJobLeaseStore>();
        store.TryRenewAsync(Arg.Any<ReviewJobLease>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<ReviewJobLeaseRenewal>(_ =>
            {
                Interlocked.Increment(ref attempts);
                throw new InvalidOperationException("database unreachable");
            });
        var time = new FakeTimeProvider();

        await using var heartbeat = Start(store, time, Options(maxFailures: 3));

        await AdvanceUntilAsync(
            time,
            () => heartbeat.LeaseLost.IsCancellationRequested,
            "Repeated renewal failure never gave up on the lease.");
        Assert.Equal(3, Volatile.Read(ref attempts));
    }

    // Two failures in a row must not be treated as a lost lease when the limit is three: the point of the
    // tolerance is that a brief outage does not cost a running review.
    [Fact]
    public async Task KeepsWorking_WhenFailuresRecoverBeforeTheLimit()
    {
        var attempts = 0;
        var store = Substitute.For<IReviewJobLeaseStore>();
        store.TryRenewAsync(Arg.Any<ReviewJobLease>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<ReviewJobLeaseRenewal>(_ =>
            {
                var attempt = Interlocked.Increment(ref attempts);
                if (attempt <= 2)
                {
                    throw new InvalidOperationException("database unreachable");
                }

                return new ReviewJobLeaseRenewal(true, DateTimeOffset.UtcNow.AddMinutes(2));
            });
        var time = new FakeTimeProvider();

        await using var heartbeat = Start(store, time, Options(maxFailures: 3));

        await AdvanceUntilAsync(
            time,
            () => Volatile.Read(ref attempts) >= 4,
            "The heartbeat never got past its transient failures.");

        Assert.False(heartbeat.IsLeaseLost);
    }

    // Each reason has to survive the trip: the holder finalises the job differently for an operator stop,
    // a supersede, and a budget cut, and reporting all three as one loses that.
    [Theory]
    [InlineData(ReviewJobStopReason.OperatorStop)]
    [InlineData(ReviewJobStopReason.Superseded)]
    [InlineData(ReviewJobStopReason.BudgetCapReached)]
    [InlineData(ReviewJobStopReason.RegistrationRevoked)]
    public async Task SurfacesTheStopReason_TheControlPlaneSentBack(ReviewJobStopReason reason)
    {
        var store = Substitute.For<IReviewJobLeaseStore>();
        store.TryRenewAsync(Arg.Any<ReviewJobLease>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(ReviewJobLeaseRenewal.StoppedBecause(reason));
        var time = new FakeTimeProvider();

        await using var heartbeat = Start(store, time);

        await AdvanceUntilAsync(
            time,
            () => heartbeat.LeaseLost.IsCancellationRequested,
            "A stop directive never reached the holder.");
        Assert.Equal(reason, heartbeat.StopReason);
    }

    [Fact]
    public async Task ReportsALostLease_AsDistinctFromAHaltedJob()
    {
        var store = Substitute.For<IReviewJobLeaseStore>();
        store.TryRenewAsync(Arg.Any<ReviewJobLease>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(ReviewJobLeaseRenewal.Rejected);
        var time = new FakeTimeProvider();

        await using var heartbeat = Start(store, time);

        await AdvanceUntilAsync(
            time,
            () => heartbeat.LeaseLost.IsCancellationRequested,
            "A rejected renewal did not signal that the lease was lost.");
        Assert.Equal(ReviewJobStopReason.LeaseNoLongerHeld, heartbeat.StopReason);
    }

    // A database outage is not a decision about the job, so the reason must not read as one.
    [Fact]
    public async Task ReportsRepeatedRenewalFailure_AsALostLeaseRatherThanADecision()
    {
        var store = Substitute.For<IReviewJobLeaseStore>();
        store.TryRenewAsync(Arg.Any<ReviewJobLease>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<ReviewJobLeaseRenewal>(_ => throw new InvalidOperationException("database unreachable"));
        var time = new FakeTimeProvider();

        await using var heartbeat = Start(store, time, Options(maxFailures: 2));

        await AdvanceUntilAsync(
            time,
            () => heartbeat.LeaseLost.IsCancellationRequested,
            "Repeated renewal failure never gave up on the lease.");
        Assert.Equal(ReviewJobStopReason.LeaseNoLongerHeld, heartbeat.StopReason);
    }

    // The holder stops renewing when it is done with the job, and disposal happens twice in the worker:
    // once explicitly before writing terminal state, once by the enclosing await using.
    [Fact]
    public async Task Dispose_IsIdempotent_AndStopsRenewing()
    {
        var store = Substitute.For<IReviewJobLeaseStore>();
        store.TryRenewAsync(Arg.Any<ReviewJobLease>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewJobLeaseRenewal(true, DateTimeOffset.UtcNow.AddMinutes(2)));
        var time = new FakeTimeProvider();

        var heartbeat = Start(store, time);
        await heartbeat.DisposeAsync();
        await heartbeat.DisposeAsync();

        time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(50, CancellationToken.None);

        await store.DidNotReceive()
            .TryRenewAsync(Arg.Any<ReviewJobLease>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }
}

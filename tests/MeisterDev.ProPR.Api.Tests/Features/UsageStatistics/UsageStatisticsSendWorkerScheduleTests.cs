// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Workers;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Models;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterDev.ProPR.Api.Tests.Features.UsageStatistics;

/// <summary>
///     How long the send loop waits after each kind of cycle.
///     <para>
///         The loop previously scheduled from the stored attempt timestamp. In the two states where nothing is
///         sent that timestamp never changes, so the wait collapsed onto its one-minute floor and the loop
///         polled the database roughly fourteen thousand times a day on an installation that had switched the
///         feature off. These cases pin each state to its own interval.
///     </para>
/// </summary>
public sealed class UsageStatisticsSendWorkerScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AfterASend_TheLoopWaitsAboutADay()
    {
        var delay = CreateWorker().ResolveNextDelay(new UsageStatisticsCycleResult(UsageStatisticsSendDecision.Sent, Now));

        Assert.InRange(
            delay,
            UsageStatisticsSendSchedule.Cadence,
            UsageStatisticsSendSchedule.Cadence + UsageStatisticsSendSchedule.Jitter);
    }

    // A cycle that sent but could not store the outcome still consumes the day. Scheduling from the stale
    // timestamp would send again a minute later, and keep sending every minute while the write failed.
    [Fact]
    public void AfterASendWhoseOutcomeCouldNotBeStored_TheLoopStillWaitsADay()
    {
        var delay = CreateWorker().ResolveNextDelay(new UsageStatisticsCycleResult(UsageStatisticsSendDecision.Sent, Now.AddDays(-9)));

        Assert.InRange(
            delay,
            UsageStatisticsSendSchedule.Cadence,
            UsageStatisticsSendSchedule.Cadence + UsageStatisticsSendSchedule.Jitter);
    }

    [Theory]
    [InlineData(UsageStatisticsSendDecision.Disabled)]
    [InlineData(UsageStatisticsSendDecision.AwaitingConsent)]
    public void WhenNothingChangesUntilAnOperatorActs_TheLoopRechecksAtTheIdleInterval(UsageStatisticsSendDecision decision)
    {
        var delay = CreateWorker().ResolveNextDelay(new UsageStatisticsCycleResult(decision, Now.AddDays(-90)));

        Assert.Equal(UsageStatisticsSendSchedule.IdleRecheckInterval, delay);
    }

    [Fact]
    public void AfterACycleThatThrew_TheLoopWaitsBeforeRetrying()
    {
        var delay = CreateWorker().ResolveNextDelay(null);

        Assert.Equal(UsageStatisticsSendSchedule.IdleRecheckInterval, delay);
    }

    [Fact]
    public void AfterFindingAnotherReplicaAlreadySentToday_TheLoopWaitsOutTheRemainder()
    {
        var delay = CreateWorker().ResolveNextDelay(new UsageStatisticsCycleResult(UsageStatisticsSendDecision.NotDue, Now.AddHours(-4)));

        Assert.InRange(delay, TimeSpan.FromHours(19), TimeSpan.FromHours(22));
    }

    private static UsageStatisticsSendWorker CreateWorker()
    {
        var services = new ServiceCollection().BuildServiceProvider();

        return new UsageStatisticsSendWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            new FixedTimeProvider(Now),
            NullLogger<UsageStatisticsSendWorker>.Instance);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }
    }
}

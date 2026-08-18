// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.UsageStatistics.Support;

namespace MeisterDev.ProPR.Application.Tests.Features.UsageStatistics;

public sealed class UsageStatisticsSendScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    // An installation that has never sent waits a random part of a day. Without that, a fleet upgraded in one
    // maintenance window would all reach the receiver in the same minute.
    [Theory]
    [InlineData(0d, 0d)]
    [InlineData(0.5d, 12d)]
    [InlineData(0.999d, 23.976d)]
    public void AnInstallationThatHasNeverSent_SpreadsItsFirstAttemptAcrossADay(double sample, double expectedHours)
    {
        var delay = UsageStatisticsSendSchedule.NextDelay(null, Now, sample);

        Assert.Equal(expectedHours, delay.TotalHours, 3);
    }

    // The jitter band uses the same formula everywhere. A per-installation offset would be stable day to day
    // and would identify the installation by its arrival time.
    [Theory]
    [InlineData(0d, 24d)]
    [InlineData(0.5d, 25d)]
    [InlineData(1d, 26d)]
    public void AnInstallationThatHasSent_WaitsADayPlusItsJitter(double sample, double expectedHours)
    {
        var delay = UsageStatisticsSendSchedule.NextDelay(Now, Now, sample);

        Assert.Equal(expectedHours, delay.TotalHours, 3);
    }

    // The band runs forward from the cadence, never back. Two cycles closer together than a day would count
    // the overlapping period in both snapshots, because the window a rate is measured over has a one-day floor.
    [Theory]
    [InlineData(0d)]
    [InlineData(0.25d)]
    [InlineData(0.5d)]
    [InlineData(1d)]
    public void TheGapBetweenTwoCycles_IsNeverShorterThanADay(double sample)
    {
        Assert.True(UsageStatisticsSendSchedule.NextDelay(Now, Now, sample) >= UsageStatisticsSendSchedule.Cadence);
    }

    // A restart mid-cycle must not restart the day. The wait is measured from the stored attempt, so the time
    // already elapsed counts.
    [Fact]
    public void ARestartPartWayThroughACycle_WaitsOnlyTheRemainder()
    {
        var delay = UsageStatisticsSendSchedule.NextDelay(Now.AddHours(-20), Now, 0.5d);

        Assert.Equal(5d, delay.TotalHours, 3);
    }

    // A stored timestamp older than a full cycle is due now, but the loop still pauses briefly so a
    // persistently failing write cannot make it spin.
    [Fact]
    public void AnOverdueInstallation_StillPausesBeforeTrying()
    {
        var delay = UsageStatisticsSendSchedule.NextDelay(Now.AddDays(-9), Now, 0.5d);

        Assert.Equal(UsageStatisticsSendSchedule.MinimumDelay, delay);
    }

    // A clock that jumped backwards, or a timestamp written by a host whose clock is ahead, must not delay the
    // loop for days.
    [Fact]
    public void ATimestampFromTheFuture_IsCappedAtOneCyclePlusJitter()
    {
        var delay = UsageStatisticsSendSchedule.NextDelay(Now.AddDays(30), Now, 0.5d);

        Assert.Equal(UsageStatisticsSendSchedule.Cadence + UsageStatisticsSendSchedule.Jitter, delay);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-3d)]
    [InlineData(7d)]
    public void ASampleOutsideTheUnitInterval_IsClamped(double sample)
    {
        var delay = UsageStatisticsSendSchedule.NextDelay(Now, Now, sample);

        Assert.InRange(
            delay,
            UsageStatisticsSendSchedule.Cadence,
            UsageStatisticsSendSchedule.Cadence + UsageStatisticsSendSchedule.Jitter);
    }
}

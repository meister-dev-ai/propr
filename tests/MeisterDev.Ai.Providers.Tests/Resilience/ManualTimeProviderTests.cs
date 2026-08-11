// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Tests.Resilience;

/// <summary>
///     Covers the hand-driven clock itself. Tests that measure waiting are only as trustworthy as the clock they
///     measure against, so the places where this one could quietly disagree with a real timer are pinned here.
/// </summary>
public sealed class ManualTimeProviderTests
{
    [Fact]
    public void ATimerFiresOnlyOnceTheClockReachesItsDueTime()
    {
        var clock = new ManualTimeProvider();
        var fired = 0;
        using var timer = clock.CreateTimer(_ => fired++, null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);

        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(0, fired);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, fired);
    }

    [Fact]
    public void ADisposedTimerRefusesToBeRescheduledAndNeverFires()
    {
        var clock = new ManualTimeProvider();
        var fired = 0;
        var timer = clock.CreateTimer(_ => fired++, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        timer.Dispose();

        Assert.False(timer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan));

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(0, fired);
    }

    [Fact]
    public void ARepeatingPeriodIsRefusedRatherThanFiredOnce()
    {
        var clock = new ManualTimeProvider();

        Assert.Throws<NotSupportedException>(() => clock.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
    }
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Runner.Contracts;
using MeisterDev.ProPR.Runner.Execution;
using Microsoft.Extensions.Time.Testing;

namespace MeisterDev.ProPR.Runner.Tests;

/// <summary>
///     The maximum review duration of the tenant reaches a runner as the time that was left when the control plane
///     issued the manifest. The runner counts it on its own clock from the moment it received the manifest, so its
///     review ends at the deadline an in-process review would end at.
/// </summary>
public sealed class ManifestTimeLimitTests
{
    [Fact]
    public void TheTimeLeftInTheManifest_IsCountedFromItsArrival()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-28T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var manifest = RunnerManifests.Sample() with { TimeLimit = new RunnerReviewTimeLimit(1800, 120) };

        var timeLimit = RunnerJobExecutor.StartTimeLimit(manifest, clock);

        Assert.NotNull(timeLimit);
        Assert.Equal(1800, timeLimit.MaxReviewDurationSeconds);
        clock.Advance(TimeSpan.FromSeconds(119));
        Assert.False(timeLimit.IsReached());
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(timeLimit.IsReached());
    }

    // The deadline can pass while the job is being prepared on the control plane. The runner then starts no file.
    [Fact]
    public void ATimeLeftThatHasRunOut_IsReachedOnArrival()
    {
        var manifest = RunnerManifests.Sample() with { TimeLimit = new RunnerReviewTimeLimit(60, -5) };

        var timeLimit = RunnerJobExecutor.StartTimeLimit(manifest, new FakeTimeProvider());

        Assert.True(timeLimit!.IsReached());
    }

    // A tenant without a duration, or a control plane from before the field, sends none, and the review runs
    // without one.
    [Fact]
    public void AManifestWithoutATimeLimit_StartsNone()
    {
        Assert.Null(RunnerJobExecutor.StartTimeLimit(RunnerManifests.Sample(), new FakeTimeProvider()));
    }
}

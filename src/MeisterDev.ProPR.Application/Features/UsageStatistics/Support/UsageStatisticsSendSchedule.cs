// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.UsageStatistics.Support;

/// <summary>
///     Decides how long to wait before the next send attempt.
///     <para>
///         The jitter is drawn from a uniform distribution each cycle, using the same formula for every
///         installation. A per-installation offset derived from the identifier would be stable across days and
///         would act as a second identifier in the receiver's arrival times.
///     </para>
/// </summary>
public static class UsageStatisticsSendSchedule
{
    /// <summary>The nominal cadence: one snapshot a day.</summary>
    public static readonly TimeSpan Cadence = TimeSpan.FromHours(24);

    /// <summary>
    ///     How far past the cadence a cycle may land.
    ///     <para>
    ///         The band runs forward only. A band that also ran backwards could place two consecutive cycles
    ///         closer together than the shortest window a rate is measured over, so the overlapping period
    ///         would be counted in both snapshots.
    ///     </para>
    /// </summary>
    public static readonly TimeSpan Jitter = TimeSpan.FromHours(2);

    /// <summary>
    ///     The shortest gap between two attempts. It also stops a restart loop from sending on every start.
    /// </summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromHours(20);

    /// <summary>A floor on the wait, so a failed state write cannot make the loop spin.</summary>
    public static readonly TimeSpan MinimumDelay = TimeSpan.FromMinutes(1);

    /// <summary>
    ///     How long to wait before looking again at an installation that is switched off or has not yet shown
    ///     an administrator the notice.
    ///     <para>
    ///         Scheduling those states from the last attempt would schedule from a timestamp that never moves,
    ///         which collapses the wait onto the one-minute floor and polls the database continuously. An hour
    ///         keeps that polling low while a toggle switched back on still takes effect the same day.
    ///     </para>
    /// </summary>
    public static readonly TimeSpan IdleRecheckInterval = TimeSpan.FromHours(1);

    /// <summary>
    ///     Returns how long to wait before the next attempt.
    ///     <para>
    ///         An installation that has never sent waits a uniformly random part of a day first, so a fleet
    ///         upgraded in one maintenance window does not reach the receiver at the same time.
    ///     </para>
    /// </summary>
    /// <param name="lastAttemptAt">When a send was last attempted, successful or not.</param>
    /// <param name="now">The current time.</param>
    /// <param name="uniformSample">A uniform sample in <c>[0, 1)</c>.</param>
    public static TimeSpan NextDelay(DateTimeOffset? lastAttemptAt, DateTimeOffset now, double uniformSample)
    {
        var sample = double.IsNaN(uniformSample) ? 0d : Math.Clamp(uniformSample, 0d, 1d);

        if (lastAttemptAt is not { } lastAttempt)
        {
            return Cadence * sample;
        }

        var interval = Cadence + (Jitter * sample);
        var remaining = lastAttempt + interval - now;

        if (remaining < MinimumDelay)
        {
            return MinimumDelay;
        }

        // Cap the wait so a backwards clock jump, or a stored timestamp from the future, does not delay the
        // loop for days.
        var ceiling = Cadence + Jitter;
        return remaining > ceiling ? ceiling : remaining;
    }
}

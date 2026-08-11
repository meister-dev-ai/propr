// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Tests.Resilience;

/// <summary>
///     A clock the test moves by hand. Timers fire only when the test advances past their due time, which is
///     what makes a waiter observable while it is still waiting.
/// </summary>
/// <remarks>
///     <see cref="RecordingTimeProvider" /> answers a different question: it fires everything at once and records
///     the schedule, which suits a test about how long a backoff asked for. A test about something being held
///     back needs the opposite, a wait that stays unfinished until the test says otherwise.
/// </remarks>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (this._sync)
        {
            return this._now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ManualTimer.RejectRepeating(period);

        var timer = new ManualTimer(this, callback, state);
        lock (this._sync)
        {
            // Registered and given its due time under one lock. Setting the due time afterwards would leave a
            // window in which an Advance running alongside sees a timer with nothing due and skips it, and the
            // wait it belongs to then never completes.
            this._timers.Add(timer);
            timer.ScheduleFrom(this._now, dueTime);
        }

        return timer;
    }

    /// <summary>Moves the clock on and fires every timer that has come due.</summary>
    /// <param name="amount">How far to move the clock.</param>
    public void Advance(TimeSpan amount)
    {
        ManualTimer[] due;
        lock (this._sync)
        {
            this._now += amount;
            due = [.. this._timers.Where(timer => timer.IsDueAt(this._now))];
        }

        // Fired outside the lock, because a callback may set another timer and would otherwise deadlock on it.
        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private readonly object _sync = new();
        private DateTimeOffset? _dueAt;
        private bool _disposed;

        /// <summary>
        ///     Refuses a period this clock would not honour. A timer here fires once, so accepting a repeating
        ///     period and firing once anyway would make the clock quietly disagree with the real one.
        /// </summary>
        /// <param name="period">The period asked for. Both spellings of "no repeat" are accepted.</param>
        public static void RejectRepeating(TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan && period != TimeSpan.Zero)
            {
                throw new NotSupportedException($"This clock fires a timer once; a period of {period} would repeat and is not honoured.");
            }
        }

        /// <summary>
        ///     Sets the due time against a clock reading the caller already holds. A disposed timer schedules
        ///     nothing, because the real one cannot be rescheduled once it is gone.
        /// </summary>
        /// <param name="now">The current time, read by a caller that holds the provider's lock.</param>
        /// <param name="dueTime">How far ahead the timer is due, or infinite to leave it unscheduled.</param>
        /// <returns>Whether the timer took the new due time.</returns>
        public bool ScheduleFrom(DateTimeOffset now, TimeSpan dueTime)
        {
            lock (this._sync)
            {
                if (this._disposed)
                {
                    return false;
                }

                this._dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : now + dueTime;
                return true;
            }
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            RejectRepeating(period);

            // Read before taking this timer's lock: Advance holds the provider's lock while inspecting timers,
            // so taking them in the other order here would be a deadlock waiting for the right interleaving.
            var now = owner.GetUtcNow();

            // Reported rather than assumed, so a test that reschedules a disposed timer sees the refusal instead
            // of a success followed by a wait that never finishes.
            return this.ScheduleFrom(now, dueTime);
        }

        public bool IsDueAt(DateTimeOffset now)
        {
            lock (this._sync)
            {
                return !this._disposed && this._dueAt is { } dueAt && dueAt <= now;
            }
        }

        public void Fire()
        {
            lock (this._sync)
            {
                if (this._disposed || this._dueAt is null)
                {
                    return;
                }

                this._dueAt = null;
            }

            callback(state);
        }

        public void Dispose()
        {
            lock (this._sync)
            {
                this._disposed = true;
                this._dueAt = null;
            }
        }

        public ValueTask DisposeAsync()
        {
            this.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

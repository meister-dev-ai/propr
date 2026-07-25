// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Tests.Resilience;

/// <summary>
///     A clock whose timers fire at once and record what they were asked to wait for. That makes the backoff
///     schedule assertable without a test spending the wall-clock seconds it describes.
/// </summary>
internal sealed class RecordingTimeProvider : TimeProvider
{
    /// <summary>Every finite delay requested, in order.</summary>
    public List<TimeSpan> RequestedDelays { get; } = [];

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        return new ImmediateTimer(this, callback, state, dueTime);
    }

    private void Record(TimeSpan dueTime)
    {
        if (dueTime >= TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
        {
            this.RequestedDelays.Add(dueTime);
        }
    }

    private sealed class ImmediateTimer : ITimer
    {
        private readonly RecordingTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private int _fired;

        public ImmediateTimer(RecordingTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime)
        {
            this._owner = owner;
            this._callback = callback;
            this._state = state;
            this.Change(dueTime, Timeout.InfiniteTimeSpan);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                return true;
            }

            this._owner.Record(dueTime);

            // Queued rather than invoked inline: the caller may still be wiring up the object the callback
            // completes, and firing reentrantly from inside CreateTimer would race with that.
            if (Interlocked.Exchange(ref this._fired, 1) == 0)
            {
                ThreadPool.QueueUserWorkItem(_ => this._callback(this._state));
            }

            return true;
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}

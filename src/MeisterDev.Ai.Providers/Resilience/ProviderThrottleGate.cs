// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;

namespace MeisterDev.Ai.Providers.Resilience;

/// <summary>
///     Holds calls back while the connection they are bound for is known to be out of quota. One throttled call
///     closes the gate for its key, and every other call routed through the same key waits out the window the
///     provider stated before it puts a request on the wire.
/// </summary>
/// <remarks>
///     <para>
///         Without this, a fan-out of concurrent calls learns about a throttle one call at a time: each one has
///         to be refused itself before it backs off, so a quota that is already exhausted gets hit as many times
///         as there are callers. The gate turns the first refusal into information the rest can act on.
///     </para>
///     <para>
///         The gate paces and nothing more. It never decides whether a call is retried or how many attempts it
///         has left, which stays with the retry stage, so the two cannot come to different conclusions about the
///         same failure.
///     </para>
///     <para>
///         State is process-local and holds at most one entry per key. An entry goes as soon as a caller has
///         waited its window out. One that nobody waits on is left behind until the sweep that runs on the next
///         gate closure reclaims it, so a process that throttles once and then goes quiet keeps that one entry
///         until something is throttled again.
///     </para>
/// </remarks>
/// <param name="timeProvider">Clock used for the window; <see langword="null" /> uses the system clock.</param>
/// <param name="maxReleaseJitter">
///     Upper bound on the random spread added to each waiter's release; <see langword="null" /> uses 250ms and
///     <see cref="TimeSpan.Zero" /> makes the release exactly reproducible, which is what tests want.
/// </param>
public sealed class ProviderThrottleGate(TimeProvider? timeProvider = null, TimeSpan? maxReleaseJitter = null)
{
    /// <summary>Ceiling on one window, above which a stated delay says more about a broken provider than a quota.</summary>
    private static readonly TimeSpan MaxWindow = TimeSpan.FromHours(1);

    private static readonly TimeSpan DefaultMaxReleaseJitter = TimeSpan.FromMilliseconds(250);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _closedUntil = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _maxReleaseJitter = maxReleaseJitter ?? DefaultMaxReleaseJitter;

    /// <summary>
    ///     Closes the gate for <paramref name="key" /> for the given window, extending an existing window but
    ///     never cutting one short. A window already in force was set by a provider refusal that has not expired,
    ///     and shortening it would send the fan-out back in before the quota has recovered.
    /// </summary>
    /// <param name="key">The connection the throttle applies to.</param>
    /// <param name="window">
    ///     How long to hold calls back for. A window of zero or less closes nothing, and one longer than an hour
    ///     is clamped to an hour.
    /// </param>
    public void CloseFor(string key, TimeSpan window)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (window <= TimeSpan.Zero)
        {
            return;
        }

        // Clamped before it becomes a deadline: adding an unbounded TimeSpan to the clock overflows, and a delay
        // beyond about 24.8 days is rejected outright, so an absurd stated wait would fail the very call it was
        // read from rather than pace it.
        var clamped = window < MaxWindow ? window : MaxWindow;
        var now = this._timeProvider.GetUtcNow();
        var until = now + clamped;

        this.SweepExpired(now);
        this._closedUntil.AddOrUpdate(key, until, (_, inForce) => inForce > until ? inForce : until);
    }

    /// <summary>
    ///     Waits until the gate for <paramref name="key" /> is open, returning at once when it already is.
    /// </summary>
    /// <remarks>
    ///     One window is waited out per call rather than looping until the gate is found open. A caller that
    ///     comes back to a gate closed again by a sibling is a caller whose own attempt will be refused and
    ///     retried, and the retry brings it back here; looping instead would let one busy connection park a
    ///     caller indefinitely, with the attempt budget none the wiser.
    /// </remarks>
    /// <param name="key">The connection the call is bound for.</param>
    /// <param name="cancellationToken">The caller's token. Cancelling leaves the gate exactly as it was.</param>
    public ValueTask WaitAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (!this._closedUntil.TryGetValue(key, out var until))
        {
            return ValueTask.CompletedTask;
        }

        var remaining = until - this._timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            this.Reopen(key, until);
            return ValueTask.CompletedTask;
        }

        return new ValueTask(this.WaitOutAsync(key, until, remaining + this.ReleaseJitter(), cancellationToken));
    }

    private async Task WaitOutAsync(string key, DateTimeOffset until, TimeSpan remaining, CancellationToken cancellationToken)
    {
        await Task.Delay(remaining, this._timeProvider, cancellationToken).ConfigureAwait(false);
        this.Reopen(key, until);
    }

    // Every waiter on a key is released off the same deadline, so an unspread release puts the whole fan-out back
    // on the wire within the same tick and can earn the refusal the gate was closed for. The reasoning is the one
    // behind ProviderRetryPolicy.JitterFactor: a spread costs milliseconds and a synchronized march costs a round.
    private TimeSpan ReleaseJitter()
    {
        return this._maxReleaseJitter <= TimeSpan.Zero
            ? TimeSpan.Zero
            : this._maxReleaseJitter * Random.Shared.NextDouble();
    }

    // Nothing else drops the entry for a key that is throttled and then never called again, so each closure
    // clears the deadlines that have passed. Closing is rare and the map holds one entry per connection, so
    // walking it costs nothing worth avoiding. The value has to match for a removal, which leaves a window that
    // was reset in the meantime alone.
    private void SweepExpired(DateTimeOffset now)
    {
        foreach (var entry in this._closedUntil)
        {
            if (entry.Value <= now)
            {
                this._closedUntil.TryRemove(entry);
            }
        }
    }

    // The entry is dropped only while it still holds the window that was waited out, so a throttle that arrived
    // in the meantime survives. Dropping it at all is what keeps the map from growing an entry for every
    // connection the process has ever seen refused.
    private void Reopen(string key, DateTimeOffset until)
    {
        this._closedUntil.TryRemove(new KeyValuePair<string, DateTimeOffset>(key, until));
    }
}

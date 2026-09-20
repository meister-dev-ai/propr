// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

/// <summary>
///     The signal a provider family watches while one of its actions is in flight, tripped when the invocation's
///     window closes and when the process is shutting down.
/// </summary>
/// <remarks>
///     <para>
///         Cooperative, and stated as such: a family that watches the signal returns at the window, and one that
///         blocks a thread without watching it is not cut. Nothing here isolates the host from a family. The
///         invocation is expired either way, so neither the call that started it nor whatever is watching it is
///         held; the thread is not reclaimed.
///     </para>
///     <para>
///         Held for the process rather than per request, because an invocation outlives the request that opened
///         it. Disposal trips every signal still live, and that covers shutdown: the container disposes this
///         while the host stops.
///     </para>
/// </remarks>
public sealed class ProviderInvocationCancellation : IDisposable
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _live = new();
    private readonly object _gate = new();

    private bool _stopping;

    /// <summary>The invocations this host has open signals for, which are the runs it started and has not closed.</summary>
    public IReadOnlyCollection<Guid> Live
    {
        get
        {
            this.DropTripped();
            return [.. this._live.Keys];
        }
    }

    /// <summary>Opens the signal for one invocation, tripped no later than <paramref name="window" /> from now.</summary>
    /// <remarks>
    ///     One signal per invocation, for the length of that invocation. A second call for a run that already has
    ///     one returns the signal it has rather than replacing it: a form submission continues the invocation it
    ///     came from, and a replacement would restart the window the first call opened, which is the window the
    ///     operator was told about.
    ///     <para>
    ///         A window at or below zero is one that has already lapsed, and the signal returned for it is
    ///         already tripped. A window beyond the host's maximum is capped at the maximum.
    ///     </para>
    /// </remarks>
    /// <param name="invocationId">The invocation the signal belongs to.</param>
    /// <param name="window">How long the invocation may run.</param>
    public CancellationToken Open(Guid invocationId, TimeSpan window)
    {
        this.DropTripped();

        lock (this._gate)
        {
            if (this._stopping)
            {
                // The sweep that trips live signals has already run, so one registered now would never be
                // tripped. An action started while the host is stopping has no window to run in either.
                return new CancellationToken(canceled: true);
            }

            // Read before the live source, not after. A continuation arriving past the invocation's deadline
            // was handed the signal the first call registered, which is still live, so an expired invocation
            // carried on acting.
            if (window <= TimeSpan.Zero)
            {
                // The invocation's window has already lapsed, so there is no time left to run in. Reading a
                // non-positive window as unspecified and granting the maximum would hand an expired invocation a
                // fully live signal and a context capable of acting on it for half an hour.
                return new CancellationToken(canceled: true);
            }

            // Reused once the window is known to be live, so a continuation of the same invocation shares the
            // signal the first call registered.
            if (this._live.TryGetValue(invocationId, out var existing))
            {
                return existing.Token;
            }

            var source = new CancellationTokenSource(
                window > ProviderHostLimits.MaximumInvocationWindow
                    ? ProviderHostLimits.MaximumInvocationWindow
                    : window);

            this._live[invocationId] = source;
            return source.Token;
        }
    }

    /// <summary>Closes the signal for one invocation that has reached its terminal state.</summary>
    /// <remarks>
    ///     Under the same lock as <see cref="Open" />, so a run being closed on one thread cannot hand a disposed
    ///     signal to a call continuing it on another.
    /// </remarks>
    /// <param name="invocationId">The invocation that has finished.</param>
    public void Close(Guid invocationId)
    {
        lock (this._gate)
        {
            if (this._live.TryRemove(invocationId, out var source))
            {
                source.Dispose();
            }
        }
    }

    /// <summary>Removes the signals that reached their own timer.</summary>
    /// <remarks>
    ///     A signal trips either because the run was closed, which calls <see cref="Close" />, or because its
    ///     window elapsed, which calls nothing: such a run is expired by the sweep that reads the table, not by a
    ///     family reporting. Without this the host would hold a source for every action that ran out of its
    ///     window, and <see cref="Live" /> would name runs it stopped following as still open, which the
    ///     stop path expires.
    /// </remarks>
    private void DropTripped()
    {
        lock (this._gate)
        {
            foreach (var (invocationId, source) in this._live)
            {
                if (!source.IsCancellationRequested)
                {
                    continue;
                }

                if (this._live.TryRemove(invocationId, out var tripped))
                {
                    tripped.Dispose();
                }
            }
        }
    }

    /// <summary>
    ///     Stops new signals being opened, without tripping the ones already live.
    /// </summary>
    /// <remarks>
    ///     Called first when the host stops, so the set of live invocations read straight after it is the set
    ///     that can still exist. Marking this only in <see cref="Dispose" /> left a window between the read and
    ///     the mark in which an invocation could open, miss the expiry sweep, and sit Pending until its own
    ///     window lapsed.
    /// </remarks>
    public void BeginStopping()
    {
        lock (this._gate)
        {
            this._stopping = true;
        }
    }

    /// <summary>Trips every signal still live. Shutdown does this.</summary>
    public void Dispose()
    {
        lock (this._gate)
        {
            this._stopping = true;
        }

        foreach (var invocationId in this._live.Keys)
        {
            if (!this._live.TryRemove(invocationId, out var source))
            {
                continue;
            }

            try
            {
                source.Cancel();
            }
            catch (AggregateException)
            {
                // A family's own continuation threw while it was being told to stop. The rest still have to be
                // told, so the failure is left with the family that produced it.
            }

            source.Dispose();
        }
    }
}

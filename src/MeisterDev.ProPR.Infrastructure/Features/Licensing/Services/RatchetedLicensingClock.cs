// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.Licensing.Services;

/// <summary>
///     Reports the later of the host clock and the highest instant the installation has recorded, and records
///     the host reading as it goes.
///     <para>
///         License terms decide whether an installation is entitled, and the host clock is something an operator
///         controls, so a reading taken from it alone would let a license whose term has ended run again after the
///         clock was set back. Comparing against a persisted value prevents that: the recorded instant survives a
///         restart, and a reading earlier than it is reported as the recorded one.
///     </para>
///     <para>
///         The row is read on every call, and each replica writes it at most once per
///         <see cref="AdvanceInterval" />, which keeps the cost of the guarantee to one query on a path that
///         already runs at a bounded rate. A first reading or one past the interval does not return until its
///         advance has persisted, because a fresh entitlement decision must not depend on an instant that a
///         restart would lose. Callers that arrive while an advance is already running wait for that one
///         instead of starting another, so a burst produces one write and every caller in it decides from the
///         instant that write persisted.
///     </para>
/// </summary>
public sealed partial class RatchetedLicensingClock : ILicensingClock
{
    /// <summary>
    ///     How far the recorded instant is allowed to fall behind the host clock before it is written forward.
    ///     <para>
    ///         The interval bounds how much of the timeline a clock rollback can recover. The recorded instant
    ///         trails the last reading by at most this much, and readings happen when the license state is
    ///         loaded, so an installation resumes from a value at most an interval behind the last load it made.
    ///         The interval is also what keeps the write rate down: each replica writes at most once per
    ///         interval, so an installation of several replicas writes that many times.
    ///     </para>
    /// </summary>
    internal static readonly TimeSpan AdvanceInterval = TimeSpan.FromHours(1);

    /// <summary>
    ///     How far the host clock may read behind the recorded instant before it is reported.
    ///     <para>
    ///         Time synchronization moves a host clock by small amounts as a matter of course, and such a step
    ///         needs no operator attention. Term evaluation is unaffected by where the tolerance sits, because a
    ///         reading behind the recorded instant is replaced by it whether it is reported or not.
    ///     </para>
    /// </summary>
    internal static readonly TimeSpan BackwardsTolerance = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     How far past real elapsed time the recorded instant may be carried forward.
    ///     <para>
    ///         The recorded instant only ever rises, so a reading from a host whose clock is wrong in the
    ///         forward direction would otherwise be permanent: every replica would judge terms at that instant,
    ///         a license with years left would read as reverted, activating a replacement would be refused, and
    ///         the only remedy is correcting the row by hand. Bounding one step is not enough on its own,
    ///         because readings happen whenever the license state is loaded and each of them would carry the
    ///         record forward again. The record is therefore held to the time this process has actually
    ///         observed passing, plus this allowance.
    ///     </para>
    ///     <para>
    ///         A host that is ahead by less than the allowance is followed exactly. One further ahead moves the
    ///         record at the rate time passes, so it catches up over its own uptime rather than in a burst, and
    ///         a wrong clock cannot carry the record past real time by more than the allowance for as long as
    ///         the process runs.
    ///     </para>
    /// </summary>
    internal static readonly TimeSpan ForwardAllowance = TimeSpan.FromDays(1);

    private readonly ILogger<RatchetedLicensingClock> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;

    // Whether the host clock was behind the recorded instant at the last reading, so a condition that persists
    // is reported once rather than on every reading.
    private int _reportedBehind;

    // Whether a host reading far ahead of the recorded instant is being reported, so a condition that persists
    // is reported once rather than on every reading and is reported again if it recurs after the host clock is
    // corrected.
    private int _reportedAhead;

    // The reading this process last recorded, and the monotonic timestamp it was recorded at. Together they
    // say how far the record may be carried forward now: a host clock cannot be used to bound itself, so the
    // elapsed time comes from a source no operator sets.
    private DateTimeOffset? _anchorInstant;
    private long _anchorTimestamp;

    // Admits one advance at a time. Concurrent callers that all read the same stale recorded value would
    // otherwise all write. They queue here instead, and the ones that arrive while a write is running find it
    // finished and take its result, so the write rate is bounded without any of them returning before an
    // advance has persisted.
    private readonly SemaphoreSlim _advanceGate = new(1, 1);

    // The instant the last successful advance persisted, or null while none has succeeded in this process.
    // Read and written under the gate. A failed advance leaves it as it was, so the next caller advances
    // again rather than skipping a write that never happened.
    private DateTimeOffset? _lastPersisted;

    // The highest instant this process has returned, in UTC ticks. Two calls can read the row at different
    // moments, and without this floor the later of them could return the earlier value and move licensing time
    // backwards between two decisions in one process.
    private long _highestReturnedTicks;

    /// <summary>Creates the clock.</summary>
    /// <param name="scopeFactory">
    ///     Resolves the store per reading. The clock is a singleton so that it can report a backwards host clock
    ///     once for the process, while the store is scoped because it holds a database context.
    /// </param>
    /// <param name="timeProvider">The host clock this reading starts from.</param>
    /// <param name="logger">Receives the report about a host clock reading behind the recorded instant.</param>
    public RatchetedLicensingClock(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<RatchetedLicensingClock> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        this._scopeFactory = scopeFactory;
        this._timeProvider = timeProvider;
        this._logger = logger;
    }

    /// <inheritdoc />
    public async Task<DateTimeOffset> GetUtcNowAsync(CancellationToken cancellationToken = default)
    {
        var now = this._timeProvider.GetUtcNow();

        using var scope = this._scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IHighestObservedTimeStore>();

        var recorded = await store.GetAsync(cancellationToken).ConfigureAwait(false);
        var highest = recorded ?? now;

        // Written when there is nothing recorded yet, and when the recorded instant has fallen an interval
        // behind. A reading earlier than the recorded instant is skipped here, and the store would keep the
        // recorded value even if it were passed one. What the advance persisted replaces the value read above,
        // because another replica may have raised the row past both of them.
        if (recorded is null || now - recorded.Value >= AdvanceInterval)
        {
            highest = await this
                .AdvanceCoalescedAsync(
                    store,
                    this.BoundedAdvance(now, recorded),
                    recorded is not null,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        this.ReportHostClockPosition(now, highest);

        return this.RaiseFloor(now > highest ? now : highest);
    }

    /// <summary>
    ///     The instant to record, held to at most one <see cref="ForwardStepLimit" /> past what is already
    ///     recorded, and reported when the host reading is further ahead than that.
    /// </summary>
    /// <remarks>
    ///     A first reading has nothing to be measured against and is recorded as it stands, which is the
    ///     installation establishing its own starting point.
    /// </remarks>
    private DateTimeOffset BoundedAdvance(DateTimeOffset now, DateTimeOffset? recorded)
    {
        if (recorded is not { } value)
        {
            // Nothing is recorded, so there is nothing to measure against and no earlier reading to protect.
            // This is the installation establishing where its timeline starts.
            return now;
        }

        var ceiling = this.ForwardCeiling(value);

        if (now <= ceiling)
        {
            Interlocked.Exchange(ref this._reportedAhead, 0);

            return now;
        }

        // Read and written in one step, because several readings can reach this together and each of them
        // would otherwise see the flag as it stood before any of them had claimed the report.
        if (Interlocked.Exchange(ref this._reportedAhead, 1) == 0)
        {
            LogHostClockFarAheadOfRecordedInstant(this._logger, now, value, ceiling);
        }

        return ceiling;
    }

    /// <summary>
    ///     The furthest instant a reading may carry the record to: what this process last recorded, plus the
    ///     time it has since observed passing, plus the allowance.
    /// </summary>
    /// <remarks>
    ///     The elapsed time comes from the monotonic timestamp rather than from the host clock, because a
    ///     clock that is wrong cannot be used to bound itself. Before this process has recorded anything the
    ///     recorded value is the only reference it has, so the ceiling is that value plus the allowance.
    /// </remarks>
    private DateTimeOffset ForwardCeiling(DateTimeOffset recorded)
    {
        if (this._anchorInstant is not { } anchor)
        {
            // Taken from the value the row already held rather than from anything this reading proposes, and
            // fixed for the life of the process. Re-anchoring on each write would add the allowance again
            // every time, which is the runaway this bound exists to stop, spread over more steps.
            this._anchorInstant = recorded;
            this._anchorTimestamp = this._timeProvider.GetTimestamp();

            return recorded + ForwardAllowance;
        }

        return anchor + this._timeProvider.GetElapsedTime(this._anchorTimestamp) + ForwardAllowance;
    }

    /// <summary>
    ///     Advances the recorded instant, admitting one caller at a time and returning what the store holds
    ///     afterwards.
    /// </summary>
    /// <remarks>
    ///     A caller that finds the gate taken waits rather than skipping the advance, so it returns only once
    ///     an advance has persisted. Once through the gate it checks what the last successful advance in this
    ///     process recorded, and takes that value when it is younger than the interval, which is what keeps a
    ///     burst to one write. A caller that found no record skips that check, because the value this process
    ///     remembers says nothing about a row that is no longer there. The value is set only after the write succeeds, so a failure leaves the next
    ///     caller to advance rather than suppressing it for an interval.
    /// </remarks>
    private async Task<DateTimeOffset> AdvanceCoalescedAsync(
        IHighestObservedTimeStore store,
        DateTimeOffset now,
        bool hasRecord,
        CancellationToken cancellationToken)
    {
        await this._advanceGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!hasRecord)
            {
                // The record was read before the gate, so another caller may have created it since. Reading
                // again here separates a burst of first readings, where one write serves all of them, from a
                // row that was removed after an advance succeeded, where taking the value this process
                // remembers would return an instant the installation no longer holds and leave the row absent
                // for the rest of the interval.
                hasRecord = await store.GetAsync(cancellationToken).ConfigureAwait(false) is not null;
            }

            if (hasRecord && this._lastPersisted is { } persisted && now - persisted < AdvanceInterval)
            {
                return persisted > now ? persisted : now;
            }

            var stored = await this.AdvanceAsync(store, now, cancellationToken).ConfigureAwait(false);
            this._lastPersisted = stored;

            return stored;
        }
        finally
        {
            this._advanceGate.Release();
        }
    }

    /// <summary>
    ///     Reports the later of the supplied instant and the highest this process has already returned, and
    ///     records the result as the new floor.
    /// </summary>
    /// <remarks>
    ///     Two calls read the row at different moments, so the one that read first can finish last. Without
    ///     this floor it would return the earlier value, and two entitlement decisions in one process would
    ///     see licensing time move backwards between them.
    /// </remarks>
    private DateTimeOffset RaiseFloor(DateTimeOffset candidate)
    {
        var candidateTicks = candidate.UtcTicks;

        while (true)
        {
            var floor = Interlocked.Read(ref this._highestReturnedTicks);

            if (candidateTicks <= floor)
            {
                return new DateTimeOffset(floor, TimeSpan.Zero);
            }

            if (Interlocked.CompareExchange(ref this._highestReturnedTicks, candidateTicks, floor) == floor)
            {
                return candidate;
            }
        }
    }

    /// <summary>
    ///     Records a reading whose persistence is required before the current license decision can proceed.
    ///     <para>
    ///         The logged failure remains visible to the operator, and the exception prevents a caller from
    ///         evaluating a term against an instant that cannot survive a restart. Cancellation is passed through
    ///         unchanged because the caller requested that no decision be completed.
    ///     </para>
    /// </summary>
    private async Task<DateTimeOffset> AdvanceAsync(
        IHighestObservedTimeStore store,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            return await store.AdvanceToAsync(now, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogObservedInstantNotRecorded(this._logger, now, exception);
            throw;
        }
    }

    /// <summary>
    ///     Reports a host clock reading behind the recorded instant, once per transition into that state rather
    ///     than on every reading, because the condition holds for as long as the clock stays behind.
    /// </summary>
    private void ReportHostClockPosition(DateTimeOffset now, DateTimeOffset highest)
    {
        var isBehind = highest - now > BackwardsTolerance;

        // Read and written in one step, because several readings can reach this together and each of them would
        // otherwise see the flag as it stood before any of them had claimed the report.
        var wasBehind = Interlocked.Exchange(ref this._reportedBehind, isBehind ? 1 : 0) == 1;

        if (isBehind && !wasBehind)
        {
            LogHostClockBehindRecordedInstant(this._logger, now, highest);
        }
    }
}

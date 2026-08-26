// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Licensing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.Licensing.Services;

/// <summary>
///     Loads the activated license, verifies it, and holds the result for a short while.
///     <para>
///         The stored document is verified on every load. Two things can change under a document that was
///         accepted once: which signers the running build accepts, and where the license stands against its
///         term and the lifecycle stages around it. Only a fresh verification reports either one. Verification
///         is offline and makes no network call, so it runs on every load.
///     </para>
///     <para>
///         The cached result ages out after <see cref="CacheDuration" />. There is no cross-replica
///         invalidation: <see cref="Invalidate" /> reaches the process that activated or removed a license,
///         and every other replica converges when its own copy ages out. That bounds how long a replica can
///         act on a superseded license to the cache duration.
///     </para>
/// </summary>
public sealed partial class CachedLicenseStateProvider : ILicenseStateProvider, IDisposable
{
    /// <summary>
    ///     How long a verified result is reused.
    ///     <para>
    ///         The duration bounds two things. An activation on one replica takes effect on the others within
    ///         it, and capability checks on a busy installation reach the database at most once per window.
    ///     </para>
    ///     <para>
    ///         The term status and the lifecycle stage a cached state carries are the ones that held at the
    ///         instant the state was loaded. A license that crosses the start of its term, the start of its
    ///         warning window, its expiry or the end of its grace window inside the window is therefore
    ///         observed at the next load, up to this long after the boundary. The stage windows either side of
    ///         a boundary are measured in days, so a minute of lag at one changes nothing a caller acts on.
    ///     </para>
    /// </summary>
    internal static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

    private readonly ILicensingClock _licensingClock;
    private readonly ILogger<CachedLicenseStateProvider> _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly LicenseVerifier _verifier;

    // The state and its monotonic load timestamp are held together so a reader outside the gate cannot pair a
    // fresh state with a stale timestamp.
    private CacheEntry? _cache;

    // Counts invalidations, so a load that started before one does not store what it read.
    private long _invalidationStamp;

    // What the last load reported, so a condition that persists is logged once rather than on every refresh.
    private LicenseStateKind? _lastLoggedKind;

    /// <summary>Creates the provider.</summary>
    /// <param name="scopeFactory">
    ///     Resolves the license store per load. The provider outlives a request scope, while the store is
    ///     scoped because it holds a database context.
    /// </param>
    /// <param name="verifier">The verifier, over the trust anchor this build carries.</param>
    /// <param name="licensingClock">Supplies the instant license terms are judged against.</param>
    /// <param name="timeProvider">Drives the cache expiry.</param>
    /// <param name="logger">Receives the warnings about a stored license that cannot be used.</param>
    public CachedLicenseStateProvider(
        IServiceScopeFactory scopeFactory,
        LicenseVerifier verifier,
        ILicensingClock licensingClock,
        TimeProvider timeProvider,
        ILogger<CachedLicenseStateProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(licensingClock);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        this._scopeFactory = scopeFactory;
        this._verifier = verifier;
        this._licensingClock = licensingClock;
        this._timeProvider = timeProvider;
        this._logger = logger;
    }

    /// <inheritdoc />
    public void Dispose() => this._refreshGate.Dispose();

    /// <inheritdoc />
    public async Task<LicenseState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        if (this.TryReadCache() is { } cached)
        {
            return cached;
        }

        await this._refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // A second check inside the gate, so callers that queued behind one load reuse its result rather
            // than each running their own.
            if (this.TryReadCache() is { } loadedWhileWaiting)
            {
                return loadedWhileWaiting;
            }

            var stampBeforeLoad = Interlocked.Read(ref this._invalidationStamp);
            var state = await this.LoadAsync(cancellationToken).ConfigureAwait(false);
            var loadedAtTimestamp = this._timeProvider.GetTimestamp();

            this.LogStateChange(state);

            // A load that was already running when a license was activated or removed read the state as it
            // stood before that change, and keeping it would hide the change for a full cache duration on the
            // replica that made it. The entry is published first and the stamp is checked afterwards: an
            // invalidation that arrives before the publish is caught by the check, and one that arrives after
            // it clears the entry itself. Checking before publishing would lose an invalidation that arrives
            // between the two statements. Either way the caller still receives what its own load read.
            Volatile.Write(ref this._cache, new CacheEntry(state, loadedAtTimestamp));

            if (Interlocked.Read(ref this._invalidationStamp) != stampBeforeLoad)
            {
                Volatile.Write(ref this._cache, null);
            }

            return state;
        }
        finally
        {
            this._refreshGate.Release();
        }
    }

    /// <inheritdoc />
    public void Invalidate()
    {
        Interlocked.Increment(ref this._invalidationStamp);
        Volatile.Write(ref this._cache, null);
    }

    private LicenseState? TryReadCache()
    {
        var entry = Volatile.Read(ref this._cache);

        return entry is not null
               && this._timeProvider.GetElapsedTime(entry.LoadedAtTimestamp, this._timeProvider.GetTimestamp()) < CacheDuration
            ? entry.State
            : null;
    }

    private async Task<LicenseState> LoadAsync(CancellationToken cancellationToken)
    {
        using var scope = this._scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IActivatedLicenseStore>();

        var stored = await store.GetAsync(cancellationToken).ConfigureAwait(false);

        if (stored is null)
        {
            return LicenseState.None();
        }

        if (!stored.IsReadable)
        {
            return LicenseState.Unreadable(stored.ActivatedAt, stored.ActivatedByUserId);
        }

        var termInstant = await this.TermEvaluationInstantAsync(cancellationToken).ConfigureAwait(false);
        var result = this._verifier.Verify(stored.CompactLicense, termInstant);

        return result.IsVerified
            ? LicenseState.Verified(result.License, termInstant, stored.ActivatedAt, stored.ActivatedByUserId)
            : LicenseState.Invalid(
                result.FailureReason.Value,
                result.FailureDetail,
                stored.ActivatedAt,
                stored.ActivatedByUserId);
    }

    /// <summary>
    ///     The instant a license term is judged against.
    ///     <para>
    ///         This is a separate reading from the one the cache expiry uses. The term decides whether an
    ///         installation is entitled, so moving the host clock backwards would extend a license whose term
    ///         has ended, while the expiry only decides how soon a replica notices a change. The licensing clock
    ///         never reports an instant earlier than the highest one the installation has recorded, and the
    ///         expiry stays on the plain reading.
    ///     </para>
    ///     <para>
    ///         The clock reaches the database, which is why it is read here rather than in
    ///         <see cref="TryReadCache" />: this runs on a cache miss, roughly once per cache duration per
    ///         replica, while a cache hit reads nothing. Reading the clock is also what records the instant, so
    ///         the ratchet advances on cache misses and not at all while the cached state is being reused.
    ///     </para>
    /// </summary>
    private Task<DateTimeOffset> TermEvaluationInstantAsync(CancellationToken cancellationToken) =>
        this._licensingClock.GetUtcNowAsync(cancellationToken);

    /// <summary>
    ///     Reports a stored license that cannot be used, once per transition rather than on every refresh.
    ///     <para>
    ///         A condition that persists is refreshed every cache duration for as long as the installation
    ///         runs, so logging each load would fill the log with one repeated line.
    ///     </para>
    /// </summary>
    private void LogStateChange(LicenseState state)
    {
        var previousKind = this._lastLoggedKind;
        this._lastLoggedKind = state.Kind;

        if (previousKind == state.Kind)
        {
            return;
        }

        if (state.Kind == LicenseStateKind.Unreadable)
        {
            LogStoredLicenseUnreadable(this._logger);
        }
        else if (state.Kind == LicenseStateKind.Invalid && state.FailureReason is { } failureReason)
        {
            LogStoredLicenseNotVerified(this._logger, failureReason, state.FailureDetail);
        }
    }

    private sealed record CacheEntry(LicenseState State, long LoadedAtTimestamp);
}

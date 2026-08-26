// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

namespace MeisterDev.ProPR.Application.Features.Licensing.Ports;

/// <summary>
///     Persistence boundary for the highest instant the installation has observed.
///     <para>
///         The value only ever moves forward. Enforcing that is the store's concern rather than the caller's,
///         because several replicas advance the same row and a caller that read before writing could otherwise
///         lower a value another replica had already raised.
///     </para>
///     <para>
///         The PostgreSQL implementation provides that through a conflict clause that keeps the greater of the
///         stored and the supplied instant, so concurrent advances from several replicas cannot lower the row.
///         The implementation for the in-memory test host reads and writes in separate steps and holds for one
///         writer at a time.
///     </para>
/// </summary>
public interface IHighestObservedTimeStore
{
    /// <summary>Reads the highest observed instant, or <see langword="null" /> when none has been recorded.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The recorded instant, or <see langword="null" />.</returns>
    Task<DateTimeOffset?> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Records an instant as observed and returns what the row holds afterwards. The stored value becomes
    ///     the later of what it held and the supplied instant, so an earlier instant leaves it unchanged and
    ///     the first call records the supplied one.
    /// </summary>
    /// <remarks>
    ///     The returned instant is what the caller must decide from. It can be later than the supplied one,
    ///     because another replica may have raised the row past it, and a caller deciding from its own reading
    ///     instead would evaluate a term against an instant below the floor the installation has already
    ///     recorded.
    /// </remarks>
    /// <param name="instant">The instant to record.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The instant the row holds once it holds at least the supplied one.</returns>
    Task<DateTimeOffset> AdvanceToAsync(DateTimeOffset instant, CancellationToken cancellationToken = default);
}

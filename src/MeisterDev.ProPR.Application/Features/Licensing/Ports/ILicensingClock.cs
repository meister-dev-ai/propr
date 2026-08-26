// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

namespace MeisterDev.ProPR.Application.Features.Licensing.Ports;

/// <summary>
///     The instant license terms are judged against.
///     <para>
///         The installation records the instant it has reached and reports the recorded value whenever the host
///         clock reads earlier, so a host clock set back does not return the installation to a term that has
///         already ended. The record trails the last reading by at most one advance interval, which is what a
///         restart resumes from: an operator who sets the clock back recovers that much of the timeline and no
///         more. Within one process the reading does not go backwards at all.
///     </para>
///     <para>
///         This is not a general-purpose clock. Timing that only decides how soon a replica notices something,
///         such as a cache expiry, reads <see cref="TimeProvider" /> directly, because a value that cannot
///         decrease would hold such a timer open for as long as the host clock stayed behind.
///     </para>
/// </summary>
public interface ILicensingClock
{
    /// <summary>
    ///     Reads the current instant for licensing, recording it as observed. The call reaches the database, so
    ///     it belongs on a path that already runs at a bounded rate rather than on a per-request one.
    ///     <para>
    ///         A read that fails is raised rather than absorbed: with no recorded instant to compare against, the
    ///         term would be judged on the host clock alone, which is what this port exists to prevent. An initial
    ///         or scheduled forward write is also raised when required, because a decision based on that new instant
    ///         must not succeed if a restart would lose it.
    ///     </para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The later of the host clock and the highest instant the installation has recorded.</returns>
    Task<DateTimeOffset> GetUtcNowAsync(CancellationToken cancellationToken = default);
}

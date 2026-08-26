// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Models;

namespace MeisterDev.ProPR.Application.Features.Licensing.Ports;

/// <summary>
///     Persistence boundary for the record of license changes an installation has made.
///     <para>
///         The records are append-only and are not removed with the license they describe, so an installation can
///         still report what it ran on before the license currently on file.
///     </para>
/// </summary>
public interface ILicenseActivationEventStore
{
    /// <summary>Appends one record.</summary>
    /// <param name="activationEvent">What happened.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the record is stored.</returns>
    Task RecordAsync(LicenseActivationEvent activationEvent, CancellationToken cancellationToken = default);

    /// <summary>Reads the most recent records, newest first.</summary>
    /// <param name="maxEvents">
    ///     How many records to read at most, greater than zero. The list grows by one record per operator action,
    ///     so it stays small on any real installation; the bound keeps the read from growing without limit on one
    ///     that has been reactivated many times.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The records, newest first.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     Thrown when <paramref name="maxEvents" /> is zero or negative. A caller asking for no record is
    ///     describing a read that cannot answer anything, so it is reported rather than returned as an empty list.
    /// </exception>
    Task<IReadOnlyList<LicenseActivationEvent>> ListRecentAsync(
        int maxEvents,
        CancellationToken cancellationToken = default);
}

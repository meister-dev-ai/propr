// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

namespace MeisterDev.ProPR.Application.Features.Licensing.Ports;

/// <summary>
///     The record of the calendar months whose counted authors went above the number the license states, one
///     row per month.
///     <para>
///         A row is written the first time a month is observed above the number, and it keeps the number the
///         license stated at that observation. Later observations of the same month raise the highest count it
///         holds and move its last-observed instant; neither moves back down.
///     </para>
///     <para>
///         The last-observed instant moves to within an hour rather than to the exact instant. A month above
///         the number is observed on every lifecycle sweep and every administration read, and writing each of
///         those would take a row lock to store a value nothing reads to that resolution. A count that rises is
///         written whenever it rises, whatever the interval.
///     </para>
///     <para>
///         Rows are kept as history. A month that later returns to at or below the number keeps its row, because
///         the row states what was observed rather than what holds now. Whether an installation is reported as
///         being above the number comes from the current month's live comparison instead, so the report clears
///         once the count is at or below the number again without anything removing a row.
///     </para>
///     <para>
///         The record is descriptive. Nothing about what the installation may run is decided from it.
///     </para>
/// </summary>
public interface IAuthorOverageStore
{
    /// <summary>
    ///     Records that one UTC month's counted authors are above the number the license states.
    /// </summary>
    /// <remarks>
    ///     The month is the one the count was taken for, stated by the caller rather than derived here. An
    ///     evaluation that runs across midnight UTC would otherwise read a count for one month and write it
    ///     into the next, where the ratchet on the row makes the wrong number permanent.
    /// </remarks>
    /// <param name="month">The first day of the UTC month the count covers.</param>
    /// <param name="licensedCount">The number the license states for authors within one calendar month.</param>
    /// <param name="observedCount">The distinct authors the month holds.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>
    ///     <see langword="true" /> when this write was the month's first observation above the number, which is
    ///     what the once-per-month report is taken from. <see langword="false" /> when the month already held a
    ///     row, including when another replica wrote it at the same moment.
    /// </returns>
    Task<bool> RecordAsync(
        DateOnly month,
        long licensedCount,
        long observedCount,
        CancellationToken cancellationToken = default);
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

namespace MeisterDev.ProPR.Application.Features.Licensing.Ports;

/// <summary>
///     Persistence boundary for the highest number of reviews seen executing at the same time on a UTC day.
///     <para>
///         Concurrent reviews are a flow quota: the number rises and falls as work is claimed and finishes, so
///         a reading taken when a report is built describes the moment of the report rather than the day. The
///         value that can be compared against the ceiling a license states is the highest the day reached,
///         which is why it is recorded as the day runs instead of sampled afterwards.
///     </para>
/// </summary>
public interface IConcurrentReviewPeakStore
{
    /// <summary>
    ///     Records how many reviews are executing now against the current UTC day, keeping whichever of that
    ///     count and the recorded one is higher.
    /// </summary>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the day's row holds at least the observed count.</returns>
    Task ObserveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Reads the peak recorded for the UTC day before the current one, or <see langword="null" /> when
    ///     that day has no record.
    /// </summary>
    /// <remarks>
    ///     The day before is what a report carries, because it is the last one that has finished: the current
    ///     day's record still rises for the rest of it, so two installations reporting at different hours
    ///     would otherwise be compared against different fractions of a day. The day is resolved where the
    ///     record is kept, so it is the same clock that wrote the rows.
    /// </remarks>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The highest count recorded for the previous UTC day, or <see langword="null" />.</returns>
    Task<long?> GetPreviousDayPeakAsync(CancellationToken cancellationToken = default);
}

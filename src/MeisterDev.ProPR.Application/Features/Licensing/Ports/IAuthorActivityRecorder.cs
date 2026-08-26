// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Models;

namespace MeisterDev.ProPR.Application.Features.Licensing.Ports;

/// <summary>
///     Puts an observed author into the current month's rollup with the exclusion decision applied.
/// </summary>
/// <remarks>
///     The completion paths call this rather than the rollup store, so the decision is made once, at the one
///     point that has the signals in hand. The store keeps the flag; it does not decide it.
/// </remarks>
public interface IAuthorActivityRecorder
{
    /// <summary>
    ///     Records the observation, deciding from its signals whether the author counts.
    /// </summary>
    /// <remarks>
    ///     Whether the author counts is decided per observation and escalates in the store: a month that holds
    ///     an author as counted and then observes the same account as automation holds them as excluded from
    ///     then on. Failures are the caller's to absorb; the completion paths log them and stand.
    /// </remarks>
    /// <param name="observation">The author and the signals the source row carries about them.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the month holds the author.</returns>
    Task RecordAsync(AuthorActivityObservation observation, CancellationToken cancellationToken = default);
}

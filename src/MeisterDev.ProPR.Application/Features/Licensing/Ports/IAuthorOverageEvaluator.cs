// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Models;

namespace MeisterDev.ProPR.Application.Features.Licensing.Ports;

/// <summary>
///     Compares the current calendar month's counted authors against the number the license states for them.
/// </summary>
public interface IAuthorOverageEvaluator
{
    /// <summary>
    ///     Evaluates the current month, recording and reporting an overage when the count is above the number.
    /// </summary>
    /// <param name="observedAuthors">
    ///     The month's counted authors and the month they were counted for, when the caller has already read
    ///     them, or <see langword="null" /> to read them here. A caller that has the number passes it so one
    ///     request counts the month once. The month travels with the count because the record is written
    ///     against it, and an evaluation that runs across midnight UTC would otherwise write one month's count
    ///     into the next.
    /// </param>
    /// <param name="cancellationToken">Cancels the evaluation.</param>
    /// <returns>
    ///     The comparison, or <see langword="null" /> when there is no number to compare against: no license in
    ///     force, a license that leaves the author limit out, or one that states it as unlimited.
    /// </returns>
    Task<AuthorOverageState?> EvaluateAsync(
        AuthorMonthCount? observedAuthors = null,
        CancellationToken cancellationToken = default);
}

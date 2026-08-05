// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Budgeting.Models;

namespace MeisterDev.ProPR.Application.Features.Budgeting;

/// <summary>
///     Reports the running USD spend accumulated in each budget scope from the persisted per-job cost and
///     the per-client daily usage samples. The result seeds a unit of work's enforcement baseline and answers
///     the admission gate's "is this scope already over its cap?" question.
/// </summary>
public interface IReviewSpendAccumulator
{
    /// <summary>
    ///     Returns the spend already accumulated in each budget scope that applies to <paramref name="subject" />,
    ///     excluding the subject's own in-flight spend, as of <paramref name="asOfDate" /> (UTC). The client scope is
    ///     the month-to-date total for the current period (it resets at the period boundary); the pull-request and
    ///     increment scopes sum the persisted per-job cost of every other unit of work sharing that pull request /
    ///     increment, whichever kind it is.
    /// </summary>
    /// <param name="subject">The unit of work whose applicable scopes to total.</param>
    /// <param name="asOfDate">The UTC date defining the current monthly period for the client scope.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ReviewSpendBaseline> GetBaselineAsync(
        ReviewSpendSubject subject,
        DateOnly asOfDate,
        CancellationToken ct = default);
}

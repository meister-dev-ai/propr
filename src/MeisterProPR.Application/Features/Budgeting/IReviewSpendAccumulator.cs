// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Budgeting.Models;
using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Application.Features.Budgeting;

/// <summary>
///     Reports the running USD review spend accumulated in each budget scope from the persisted per-job cost and
///     the per-client daily usage samples. The result seeds a job's enforcement baseline and answers the
///     admission gate's "is this scope already over its cap?" question.
/// </summary>
public interface IReviewSpendAccumulator
{
    /// <summary>
    ///     Returns the review spend already accumulated in each budget scope that applies to <paramref name="job" />,
    ///     excluding the job's own in-flight spend, as of <paramref name="asOfDate" /> (UTC). The client scope is the
    ///     month-to-date total for the current period (it resets at the period boundary); the pull-request and
    ///     increment scopes sum the persisted per-job cost of the other jobs sharing that pull request / increment.
    /// </summary>
    /// <param name="job">The job whose applicable scopes to total.</param>
    /// <param name="asOfDate">The UTC date defining the current monthly period for the client scope.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ReviewSpendBaseline> GetBaselineAsync(ReviewJob job, DateOnly asOfDate, CancellationToken ct = default);
}

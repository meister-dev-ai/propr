// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Budgeting.Models;

/// <summary>
///     A point-in-time snapshot of the review spend already accumulated in each budget scope that applies to a
///     review job, excluding that job's own in-flight spend. Enforcement adds the job's live in-run spend delta on
///     top of these baselines to decide whether a cap has been reached.
/// </summary>
/// <param name="ClientMonthToDate">The client's spend for the current monthly period.</param>
/// <param name="PullRequest">The spend across the other jobs of the same pull request.</param>
/// <param name="Increment">The spend across the other jobs of the same pull-request increment.</param>
/// <param name="TenantMonthToDate">
///     The spend of every client in the job's tenant for the current monthly period. The tenant scope is the
///     widest one, so it is evaluated after the client scopes and only binds when no client cap binds first.
/// </param>
public sealed record ReviewSpendBaseline(
    ReviewScopeSpend ClientMonthToDate,
    ReviewScopeSpend PullRequest,
    ReviewScopeSpend Increment,
    ReviewScopeSpend TenantMonthToDate);

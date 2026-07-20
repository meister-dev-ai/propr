// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Budgeting.Models;

/// <summary>
///     The USD budget caps resolved for a review job's client. Every cap is optional; a null cap means no limit
///     for that scope and threshold. Soft and hard caps are independent dollar values (e.g. $80 soft / $100 hard).
///     The increment scope is governed by a hard cap only, since a single increment is one job that cannot be
///     held once started.
/// </summary>
public sealed record BudgetCaps(
    decimal? MonthlySoftCapUsd,
    decimal? MonthlyHardCapUsd,
    decimal? PullRequestSoftCapUsd,
    decimal? PullRequestHardCapUsd,
    decimal? IncrementHardCapUsd)
{
    /// <summary>Caps with no configured limits (the opt-in default: nothing is enforced).</summary>
    public static BudgetCaps None { get; } = new(null, null, null, null, null);

    /// <summary>True when at least one cap is configured, so enforcement applies to the job.</summary>
    public bool AnyConfigured =>
        this.MonthlySoftCapUsd is not null ||
        this.MonthlyHardCapUsd is not null ||
        this.PullRequestSoftCapUsd is not null ||
        this.PullRequestHardCapUsd is not null ||
        this.IncrementHardCapUsd is not null;

    /// <summary>True when at least one hard cap is configured (the only kind the in-run gate enforces).</summary>
    public bool AnyHardCapConfigured =>
        this.MonthlyHardCapUsd is not null ||
        this.PullRequestHardCapUsd is not null ||
        this.IncrementHardCapUsd is not null;
}

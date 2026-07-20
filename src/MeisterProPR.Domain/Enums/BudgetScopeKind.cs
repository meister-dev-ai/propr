// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>The granularity at which a USD budget cap is enforced.</summary>
public enum BudgetScopeKind
{
    /// <summary>The client's spend across the current monthly period.</summary>
    ClientMonthly = 0,

    /// <summary>The spend across all review jobs of a single pull request.</summary>
    PullRequest = 1,

    /// <summary>The spend across the review jobs of a single pull-request increment.</summary>
    Increment = 2,
}

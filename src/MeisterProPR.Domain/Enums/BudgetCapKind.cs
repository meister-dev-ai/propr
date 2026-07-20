// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>Which of a scope's two independent USD thresholds a budget decision refers to.</summary>
public enum BudgetCapKind
{
    /// <summary>The soft cap: stops admitting new jobs; running jobs finish.</summary>
    Soft = 0,

    /// <summary>The hard cap: cuts further model calls mid-review.</summary>
    Hard = 1,
}

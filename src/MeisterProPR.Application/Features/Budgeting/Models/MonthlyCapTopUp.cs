// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Budgeting.Models;

/// <summary>
///     The extra monthly allowance manual spend resets granted within one budget period. Amounts are additive
///     dollar totals, so a period with no resets carries <see cref="None" /> (zero on both scopes).
/// </summary>
/// <param name="SoftUsd">Total soft-cap allowance granted in the period.</param>
/// <param name="HardUsd">Total hard-cap allowance granted in the period.</param>
public sealed record MonthlyCapTopUp(decimal SoftUsd, decimal HardUsd)
{
    /// <summary>No allowance granted — the state of a period without resets.</summary>
    public static MonthlyCapTopUp None { get; } = new(0m, 0m);

    /// <summary>
    ///     True when the period carries any granted allowance. Tested against zero rather than for a positive value
    ///     so this never disagrees with applying the amounts directly.
    /// </summary>
    public bool Any => this.SoftUsd != 0m || this.HardUsd != 0m;
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterProPR.Application.Features.Budgeting.Models;
using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Application.Features.Budgeting;

/// <summary>
///     The single rule that turns a client's configured monthly caps into the caps actually in force for a budget
///     period: the configured cap plus every allowance a manual spend reset granted in that period. Every consumer —
///     guardrail enforcement, the per-client FinOps surfaces, and the tenant-wide views — composes caps through here
///     so no surface can report a different ceiling than the one enforcement applies.
/// </summary>
public static class MonthlyBudgetTopUp
{
    /// <summary>
    ///     Totals the allowances granted by <paramref name="resets" />. Callers pass the resets of exactly one
    ///     period; a scope that carried no configured cap when a reset was performed contributes nothing.
    /// </summary>
    public static MonthlyCapTopUp SumTopUps(IEnumerable<BudgetSpendReset> resets)
    {
        ArgumentNullException.ThrowIfNull(resets);

        var soft = 0m;
        var hard = 0m;
        foreach (var reset in resets)
        {
            soft += reset.TopUpSoftCapUsd ?? 0m;
            hard += reset.TopUpHardCapUsd ?? 0m;
        }

        return soft == 0m && hard == 0m ? MonthlyCapTopUp.None : new MonthlyCapTopUp(soft, hard);
    }

    /// <summary>
    ///     Applies <paramref name="topUp" /> to the monthly scope of <paramref name="configured" />. An unset cap
    ///     stays unset: "no limit" cannot be topped up, and inventing a ceiling there would start enforcing a client
    ///     that opted out. The per-pull-request and per-increment scopes are not period-based and pass through
    ///     untouched.
    /// </summary>
    public static BudgetCaps Apply(BudgetCaps configured, MonthlyCapTopUp topUp)
    {
        ArgumentNullException.ThrowIfNull(configured);
        ArgumentNullException.ThrowIfNull(topUp);

        if (!topUp.Any)
        {
            return configured;
        }

        return configured with
        {
            MonthlySoftCapUsd = ApplyTo(configured.MonthlySoftCapUsd, topUp.SoftUsd),
            MonthlyHardCapUsd = ApplyTo(configured.MonthlyHardCapUsd, topUp.HardUsd),
        };
    }

    /// <summary>Adds a granted allowance to one configured cap, leaving an unset (uncapped) scope unset.</summary>
    public static decimal? ApplyTo(decimal? configuredCapUsd, decimal topUpUsd) =>
        configuredCapUsd is null ? null : configuredCapUsd.Value + topUpUsd;
}

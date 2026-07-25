// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterProPR.Application.Features.Budgeting.Models;

namespace MeisterProPR.Application.Features.Budgeting;

/// <summary>
///     Resolves the USD budget caps in force for a client. The monthly caps returned are effective caps: the
///     configured cap plus any allowance manual spend resets granted in the period, so enforcement and reporting
///     always agree on the ceiling.
/// </summary>
public interface IBudgetCapsProvider
{
    /// <summary>
    ///     Returns the caps in force for <paramref name="clientId" /> in the current monthly period, or
    ///     <see cref="BudgetCaps.None" /> when the client is unknown or has no caps set (the opt-in default: nothing
    ///     is enforced).
    /// </summary>
    Task<BudgetCaps> GetCapsAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>
    ///     Returns the caps as configured on the client, without any manual-reset allowance applied. Reporting
    ///     surfaces use this as the baseline and add each period's own allowance on top with
    ///     <see cref="MonthlyBudgetTopUp" /> — they hold the period's resets already, so composing there costs no
    ///     extra query. It is never the ceiling enforcement applies.
    /// </summary>
    Task<BudgetCaps> GetConfiguredCapsAsync(Guid clientId, CancellationToken ct = default);
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Features.Budgeting;

/// <summary>The outcome of a manual spend reset, with the recorded row when one was written.</summary>
/// <param name="Outcome">Why the reset was applied or refused.</param>
/// <param name="Reset">The recorded reset, or null when nothing was written.</param>
public sealed record BudgetSpendResetResult(BudgetSpendResetOutcome Outcome, BudgetSpendReset? Reset);

/// <summary>Grants a client's current monthly budget period a fresh allowance on top of what it has consumed.</summary>
public interface IClientBudgetResetService
{
    /// <summary>
    ///     Records a manual spend reset for <paramref name="clientId" />'s current monthly period, granting an extra
    ///     allowance equal to the caps configured at this moment. Spend-to-date is never altered — only the period's
    ///     ceiling rises. <paramref name="actorUserId" /> is the administrator performing it, or null when the acting
    ///     identity cannot be resolved.
    /// </summary>
    Task<BudgetSpendResetResult> ResetAsync(Guid clientId, Guid? actorUserId, CancellationToken ct = default);
}

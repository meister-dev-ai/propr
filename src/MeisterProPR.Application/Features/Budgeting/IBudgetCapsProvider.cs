// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Budgeting.Models;

namespace MeisterProPR.Application.Features.Budgeting;

/// <summary>Resolves the USD budget caps configured for a client.</summary>
public interface IBudgetCapsProvider
{
    /// <summary>
    ///     Returns the budget caps configured for <paramref name="clientId" />, or <see cref="BudgetCaps.None" />
    ///     when the client is unknown or has no caps set (the opt-in default: nothing is enforced).
    /// </summary>
    Task<BudgetCaps> GetCapsAsync(Guid clientId, CancellationToken ct = default);
}

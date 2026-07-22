// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Persistence contract for budget events (cap-reached transitions) and their consumption.</summary>
public interface IBudgetEventRepository
{
    /// <summary>Persists a single budget event.</summary>
    Task AddAsync(BudgetEvent budgetEvent, CancellationToken ct);

    /// <summary>
    ///     Returns the budget events for <paramref name="clientId" /> that occurred at or after
    ///     <paramref name="sinceUtc" />, ordered by occurrence time ascending. The poll contract for a consumer.
    /// </summary>
    Task<IReadOnlyList<BudgetEvent>> GetByClientSinceAsync(Guid clientId, DateTime sinceUtc, CancellationToken ct);
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Entities;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Persistence contract for manual spend resets — the append-only rows that both grant a period's extra
///     allowance and record who granted it.
/// </summary>
public interface IBudgetSpendResetRepository
{
    /// <summary>Persists a single spend reset.</summary>
    Task AddAsync(BudgetSpendReset reset, CancellationToken ct);

    /// <summary>
    ///     Returns the resets one client received in the period starting at <paramref name="periodStart" />, ordered
    ///     by occurrence ascending.
    /// </summary>
    Task<IReadOnlyList<BudgetSpendReset>> GetByClientAndPeriodAsync(Guid clientId, DateOnly periodStart, CancellationToken ct);

    /// <summary>
    ///     Returns the resets the given clients received in every period from <paramref name="fromPeriodStart" /> to
    ///     <paramref name="toPeriodStart" /> inclusive, in one query so multi-client and multi-month surfaces avoid a
    ///     per-row round-trip. An empty client list yields an empty result without querying.
    /// </summary>
    Task<IReadOnlyList<BudgetSpendReset>> GetForClientsInRangeAsync(
        IReadOnlyCollection<Guid> clientIds,
        DateOnly fromPeriodStart,
        DateOnly toPeriodStart,
        CancellationToken ct);
}

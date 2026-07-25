// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.Budgeting;

/// <summary>EF Core persistence for <see cref="BudgetSpendReset" /> rows.</summary>
public sealed class BudgetSpendResetRepository(IDbContextFactory<MeisterProPRDbContext> contextFactory)
    : IBudgetSpendResetRepository
{
    /// <inheritdoc />
    public async Task AddAsync(BudgetSpendReset reset, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reset);

        await using var context = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        context.BudgetSpendResets.Add(reset);
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BudgetSpendReset>> GetByClientAndPeriodAsync(
        Guid clientId,
        DateOnly periodStart,
        CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await context.BudgetSpendResets
            .AsNoTracking()
            .Where(reset => reset.ClientId == clientId && reset.PeriodStart == periodStart)
            .OrderBy(reset => reset.PerformedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BudgetSpendReset>> GetForClientsInRangeAsync(
        IReadOnlyCollection<Guid> clientIds,
        DateOnly fromPeriodStart,
        DateOnly toPeriodStart,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(clientIds);

        if (clientIds.Count == 0)
        {
            return [];
        }

        await using var context = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await context.BudgetSpendResets
            .AsNoTracking()
            .Where(reset => clientIds.Contains(reset.ClientId)
                            && reset.PeriodStart >= fromPeriodStart
                            && reset.PeriodStart <= toPeriodStart)
            .OrderBy(reset => reset.PerformedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }
}

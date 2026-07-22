// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Features.Budgeting;

/// <summary>EF Core persistence for <see cref="BudgetEvent" /> rows.</summary>
public sealed class BudgetEventRepository(IDbContextFactory<MeisterProPRDbContext> contextFactory) : IBudgetEventRepository
{
    /// <inheritdoc />
    public async Task AddAsync(BudgetEvent budgetEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(budgetEvent);

        await using var context = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        context.BudgetEvents.Add(budgetEvent);
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BudgetEvent>> GetByClientSinceAsync(Guid clientId, DateTime sinceUtc, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await context.BudgetEvents
            .AsNoTracking()
            .Where(budgetEvent => budgetEvent.ClientId == clientId && budgetEvent.OccurredAt >= sinceUtc)
            .OrderBy(budgetEvent => budgetEvent.OccurredAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }
}

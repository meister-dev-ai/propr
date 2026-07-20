// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Budgeting;
using MeisterProPR.Application.Features.Budgeting.Models;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Features.Budgeting;

/// <summary>Reads a client's configured USD budget caps from its persisted record.</summary>
public sealed class BudgetCapsProvider(IDbContextFactory<MeisterProPRDbContext> contextFactory) : IBudgetCapsProvider
{
    /// <inheritdoc />
    public async Task<BudgetCaps> GetCapsAsync(Guid clientId, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var caps = await context.Clients
            .AsNoTracking()
            .Where(client => client.Id == clientId)
            .Select(client => new BudgetCaps(
                client.MonthlyBudgetSoftCapUsd,
                client.MonthlyBudgetHardCapUsd,
                client.PullRequestBudgetSoftCapUsd,
                client.PullRequestBudgetHardCapUsd,
                client.IncrementBudgetHardCapUsd))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return caps ?? BudgetCaps.None;
    }
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Budgeting;
using MeisterDev.ProPR.Application.Features.Budgeting.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.Budgeting;

/// <summary>
///     Reads a client's configured USD budget caps from its persisted record and raises the monthly caps by the
///     allowance any manual spend resets granted in the period. Caps exist to protect an installation from spend, so
///     this read path does not consult the Budgeting capability: a cap already configured keeps blocking spend in
///     every edition and at every licensing stage, including after a license has expired. Setting, editing and
///     viewing budgets stay licensed and are gated on their own surfaces.
/// </summary>
public sealed class BudgetCapsProvider(
    IDbContextFactory<MeisterProPRDbContext> contextFactory,
    IBudgetSpendResetRepository resetRepository,
    TimeProvider timeProvider) : IBudgetCapsProvider
{
    /// <inheritdoc />
    public async Task<BudgetCaps> GetCapsAsync(Guid clientId, CancellationToken ct = default)
    {
        var configured = await this.GetConfiguredCapsAsync(clientId, ct).ConfigureAwait(false);

        // An uncapped monthly scope cannot be topped up, so an opted-out client costs no reset lookup on the
        // per-review hot path.
        if (configured.MonthlySoftCapUsd is null && configured.MonthlyHardCapUsd is null)
        {
            return configured;
        }

        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var resets = await resetRepository
            .GetByClientAndPeriodAsync(clientId, new DateOnly(today.Year, today.Month, 1), ct)
            .ConfigureAwait(false);
        return MonthlyBudgetTopUp.Apply(configured, MonthlyBudgetTopUp.SumTopUps(resets));
    }

    /// <inheritdoc />
    public async Task<BudgetCaps> GetConfiguredCapsAsync(Guid clientId, CancellationToken ct = default)
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
                client.IncrementBudgetSoftCapUsd,
                client.IncrementBudgetHardCapUsd))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return caps ?? BudgetCaps.None;
    }
}

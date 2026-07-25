// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterProPR.Application.Features.Budgeting;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Features.Budgeting;

/// <summary>
///     Records a manual spend reset for a client's current monthly period. The allowance granted is the cap
///     configured at the moment of the reset, snapshotted onto the row so a later cap edit never rewrites what was
///     granted. The row's before/after effective caps are the audit trail of the change.
/// </summary>
public sealed class ClientBudgetResetService(
    IDbContextFactory<MeisterProPRDbContext> contextFactory,
    IBudgetSpendResetRepository resetRepository,
    TimeProvider timeProvider) : IClientBudgetResetService
{
    /// <inheritdoc />
    public async Task<BudgetSpendResetResult> ResetAsync(Guid clientId, Guid? actorUserId, CancellationToken ct = default)
    {
        var performedAt = timeProvider.GetUtcNow().UtcDateTime;
        var periodStart = new DateOnly(performedAt.Year, performedAt.Month, 1);

        await using var context = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var configured = await context.Clients
            .AsNoTracking()
            .Where(client => client.Id == clientId)
            .Select(client => new
            {
                Soft = client.MonthlyBudgetSoftCapUsd,
                Hard = client.MonthlyBudgetHardCapUsd,
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (configured is null)
        {
            return new BudgetSpendResetResult(BudgetSpendResetOutcome.ClientNotFound, null);
        }

        // A cap of zero is a legal "block everything" configuration, but topping it up grants nothing: the reset
        // would report success, stamp the period, and change no behaviour. Refuse it like an absent cap.
        if (configured.Soft is null or 0m && configured.Hard is null or 0m)
        {
            return new BudgetSpendResetResult(BudgetSpendResetOutcome.NoMonthlyCapConfigured, null);
        }

        var granted = await resetRepository.GetByClientAndPeriodAsync(clientId, periodStart, ct).ConfigureAwait(false);
        var alreadyGranted = MonthlyBudgetTopUp.SumTopUps(granted);
        var softBefore = MonthlyBudgetTopUp.ApplyTo(configured.Soft, alreadyGranted.SoftUsd);
        var hardBefore = MonthlyBudgetTopUp.ApplyTo(configured.Hard, alreadyGranted.HardUsd);

        var reset = new BudgetSpendReset
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            PeriodStart = periodStart,
            TopUpSoftCapUsd = configured.Soft,
            TopUpHardCapUsd = configured.Hard,
            EffectiveSoftCapBeforeUsd = softBefore,
            EffectiveSoftCapAfterUsd = softBefore is null ? null : softBefore.Value + configured.Soft!.Value,
            EffectiveHardCapBeforeUsd = hardBefore,
            EffectiveHardCapAfterUsd = hardBefore is null ? null : hardBefore.Value + configured.Hard!.Value,
            ActorUserId = actorUserId,
            PerformedAt = performedAt,
        };

        await resetRepository.AddAsync(reset, ct).ConfigureAwait(false);
        return new BudgetSpendResetResult(BudgetSpendResetOutcome.Applied, reset);
    }
}

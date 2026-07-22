// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Budgeting;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Services;

namespace MeisterProPR.Infrastructure.Features.Budgeting;

/// <summary>
///     Composes a client's monthly budget consumption from the configured caps
///     (<see cref="IBudgetCapsProvider" />) and the per-client daily usage samples, and projects the full-period
///     spend with <see cref="BudgetForecastCalculator" />. The current period is the calendar month (UTC), so the
///     total resets at the month boundary — the same period the guardrail phase enforces against. All spend sums
///     are null-aware: unpriced usage is omitted from the total and flags the result approximate.
/// </summary>
public sealed class ClientBudgetConsumptionService(
    IBudgetCapsProvider capsProvider,
    IClientTokenUsageRepository usageRepository,
    TimeProvider timeProvider) : IClientBudgetConsumptionService
{
    /// <inheritdoc />
    public async Task<ClientBudgetConsumptionDto> GetConsumptionAsync(Guid clientId, CancellationToken ct = default)
    {
        var caps = await capsProvider.GetCapsAsync(clientId, ct).ConfigureAwait(false);

        var asOf = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var periodStart = new DateOnly(asOf.Year, asOf.Month, 1);
        var daysInPeriod = DateTime.DaysInMonth(asOf.Year, asOf.Month);
        var periodEnd = new DateOnly(asOf.Year, asOf.Month, daysInPeriod);
        var nextResetOn = periodStart.AddMonths(1);

        var samples = await usageRepository
            .GetByClientAndDateRangeAsync(clientId, periodStart, asOf, ct)
            .ConfigureAwait(false);

        var spentToDate = samples
            .Where(sample => sample.EstimatedCostUsd.HasValue)
            .Sum(sample => sample.EstimatedCostUsd!.Value);
        var spendIsApproximate = samples.Any(sample => !sample.EstimatedCostUsd.HasValue);

        var dailySpend = samples
            .GroupBy(sample => sample.Date)
            .OrderBy(group => group.Key)
            .Select(group => new BudgetDailySpendDto(
                group.Key,
                group.Where(sample => sample.EstimatedCostUsd.HasValue).Sum(sample => sample.EstimatedCostUsd!.Value)))
            .ToList();

        var projectedPeriodSpend = BudgetForecastCalculator.ProjectPeriodSpend(spentToDate, asOf.Day, daysInPeriod);

        return new ClientBudgetConsumptionDto(
            clientId,
            periodStart,
            periodEnd,
            nextResetOn,
            asOf,
            spentToDate,
            spendIsApproximate,
            caps.MonthlySoftCapUsd,
            caps.MonthlyHardCapUsd,
            projectedPeriodSpend,
            dailySpend);
    }
}

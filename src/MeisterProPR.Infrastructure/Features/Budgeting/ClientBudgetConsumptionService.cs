// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Budgeting;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Services;

namespace MeisterProPR.Infrastructure.Features.Budgeting;

/// <summary>
///     Composes a client's monthly budget consumption from the configured caps
///     (<see cref="IBudgetCapsProvider" />) and the per-client daily usage samples, and projects the full-period
///     spend with <see cref="BudgetForecastCalculator" />. A period is the calendar month (UTC), so the total resets
///     at the month boundary — the same period the guardrail phase enforces against. All spend sums are null-aware:
///     unpriced usage is omitted from the total and flags the result approximate.
/// </summary>
public sealed class ClientBudgetConsumptionService(
    IBudgetCapsProvider capsProvider,
    IClientTokenUsageRepository usageRepository,
    TimeProvider timeProvider) : IClientBudgetConsumptionService
{
    private const int MinHistoryMonths = 1;
    private const int MaxHistoryMonths = 24;

    /// <inheritdoc />
    public async Task<ClientBudgetConsumptionDto> GetConsumptionAsync(
        Guid clientId,
        int? year = null,
        int? month = null,
        CancellationToken ct = default)
    {
        var caps = await capsProvider.GetCapsAsync(clientId, ct).ConfigureAwait(false);

        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var targetYear = year ?? today.Year;
        var targetMonth = month ?? today.Month;

        var periodStart = new DateOnly(targetYear, targetMonth, 1);
        var daysInPeriod = DateTime.DaysInMonth(targetYear, targetMonth);
        var periodEnd = new DateOnly(targetYear, targetMonth, daysInPeriod);
        // The last representable month (9999-12) has no next-month date; clamp so an out-of-range future period
        // passed directly to the API does not overflow DateOnly.
        var nextResetOn = periodStart < new DateOnly(9999, 12, 1) ? periodStart.AddMonths(1) : periodEnd;
        var currentMonthStart = new DateOnly(today.Year, today.Month, 1);

        // Current month: measured up to today, with a trajectory forecast. Past month: the whole (already-complete)
        // month, no forecast. Future month: nothing has been spent yet.
        var isCurrentMonth = periodStart == currentMonthStart;
        var isFutureMonth = periodStart > currentMonthStart;
        var asOf = isCurrentMonth ? today : isFutureMonth ? periodStart : periodEnd;

        IReadOnlyList<ClientTokenUsageSample> samples = isFutureMonth
            ? []
            : await usageRepository.GetByClientAndDateRangeAsync(clientId, periodStart, asOf, ct).ConfigureAwait(false);

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

        var projectedPeriodSpend = isCurrentMonth
            ? BudgetForecastCalculator.ProjectPeriodSpend(spentToDate, today.Day, daysInPeriod)
            : null;

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

    /// <inheritdoc />
    public async Task<ClientBudgetHistoryDto> GetHistoryAsync(Guid clientId, int monthsBack, CancellationToken ct = default)
    {
        var caps = await capsProvider.GetCapsAsync(clientId, ct).ConfigureAwait(false);

        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var currentMonthStart = new DateOnly(today.Year, today.Month, 1);
        var clampedMonths = Math.Clamp(monthsBack, MinHistoryMonths, MaxHistoryMonths);
        var firstMonthStart = currentMonthStart.AddMonths(-(clampedMonths - 1));

        var samples = await usageRepository
            .GetByClientAndDateRangeAsync(clientId, firstMonthStart, today, ct)
            .ConfigureAwait(false);

        var byMonth = samples
            .GroupBy(sample => new { sample.Date.Year, sample.Date.Month })
            .ToDictionary(
                group => (group.Key.Year, group.Key.Month),
                group => (
                    Spent: group.Where(sample => sample.EstimatedCostUsd.HasValue).Sum(sample => sample.EstimatedCostUsd!.Value),
                    Approximate: group.Any(sample => !sample.EstimatedCostUsd.HasValue)));

        var months = new List<BudgetMonthSpendDto>(clampedMonths);
        for (var offset = 0; offset < clampedMonths; offset++)
        {
            var monthStart = firstMonthStart.AddMonths(offset);
            byMonth.TryGetValue((monthStart.Year, monthStart.Month), out var bucket);
            months.Add(new BudgetMonthSpendDto(monthStart.Year, monthStart.Month, monthStart, bucket.Spent, bucket.Approximate));
        }

        return new ClientBudgetHistoryDto(clientId, caps.MonthlySoftCapUsd, caps.MonthlyHardCapUsd, months);
    }
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Budgeting;
using MeisterProPR.Application.Features.Budgeting.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Services;

namespace MeisterProPR.Infrastructure.Features.Budgeting;

/// <summary>
///     Composes a tenant's aggregate spend from the caps in force for its clients (manual-reset allowance included)
///     and a single per-month cost rollup across those clients, projecting the current-period aggregate with
///     <see cref="BudgetForecastCalculator" />. The caps reported are the sum of the clients' monthly caps (a
///     reference total, since budgets are per client).
/// </summary>
public sealed class TenantBudgetSpendService(
    IClientAdminService clientAdminService,
    IClientTokenUsageRepository usageRepository,
    IBudgetSpendResetRepository resetRepository,
    TimeProvider timeProvider) : ITenantBudgetSpendService
{
    private const int MinHistoryMonths = 1;
    private const int MaxHistoryMonths = 24;

    /// <inheritdoc />
    public async Task<TenantSpendDto> GetSpendAsync(Guid tenantId, int monthsBack, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var currentMonthStart = new DateOnly(today.Year, today.Month, 1);
        var daysInPeriod = DateTime.DaysInMonth(today.Year, today.Month);
        var periodEnd = new DateOnly(today.Year, today.Month, daysInPeriod);
        var clampedMonths = Math.Clamp(monthsBack, MinHistoryMonths, MaxHistoryMonths);
        var firstMonthStart = currentMonthStart.AddMonths(-(clampedMonths - 1));

        var clients = (await clientAdminService.GetAllAsync(ct).ConfigureAwait(false))
            .Where(client => client.TenantId == tenantId)
            .ToList();
        var clientIds = clients.Select(client => client.Id).ToList();

        // Every month's manual resets for every client in one query, so each month in the trend can be summed
        // against the ceilings that were in force for it rather than against today's.
        var resetsInRange = await resetRepository
            .GetForClientsInRangeAsync(clientIds, firstMonthStart, currentMonthStart, ct)
            .ConfigureAwait(false);
        var topUpByClientMonth = resetsInRange
            .GroupBy(reset => (reset.ClientId, reset.PeriodStart))
            .ToDictionary(group => group.Key, group => MonthlyBudgetTopUp.SumTopUps(group.ToList()));

        // Sums the caps in force across the tenant's clients for one month; null when no client caps that scope.
        (decimal? Soft, decimal? Hard) SumCapsFor(DateOnly monthStart)
        {
            decimal softTotal = 0m, hardTotal = 0m;
            bool anySoft = false, anyHard = false;
            foreach (var client in clients)
            {
                var topUp = topUpByClientMonth.TryGetValue((client.Id, monthStart), out var found)
                    ? found
                    : MonthlyCapTopUp.None;
                var caps = client.BudgetConfigOrEmpty;

                if (MonthlyBudgetTopUp.ApplyTo(caps.MonthlySoftCapUsd, topUp.SoftUsd) is { } soft)
                {
                    softTotal += soft;
                    anySoft = true;
                }

                if (MonthlyBudgetTopUp.ApplyTo(caps.MonthlyHardCapUsd, topUp.HardUsd) is { } hard)
                {
                    hardTotal += hard;
                    anyHard = true;
                }
            }

            return (anySoft ? softTotal : null, anyHard ? hardTotal : null);
        }

        var costByMonth = await usageRepository
            .GetMonthlyCostForClientsAsync(clientIds, firstMonthStart, today, ct)
            .ConfigureAwait(false);

        var months = new List<TenantSpendMonthDto>(clampedMonths);
        for (var offset = 0; offset < clampedMonths; offset++)
        {
            var monthStart = firstMonthStart.AddMonths(offset);
            costByMonth.TryGetValue((monthStart.Year, monthStart.Month), out var spent);
            var (monthSoft, monthHard) = SumCapsFor(monthStart);
            months.Add(
                new TenantSpendMonthDto(
                    monthStart.Year,
                    monthStart.Month,
                    monthStart,
                    spent,
                    monthSoft,
                    monthHard,
                    resetsInRange.Count(reset => reset.PeriodStart == monthStart)));
        }

        costByMonth.TryGetValue((today.Year, today.Month), out var spentToDate);
        var (currentSoft, currentHard) = SumCapsFor(currentMonthStart);
        var resetsThisPeriod = resetsInRange.Where(reset => reset.PeriodStart == currentMonthStart).ToList();

        return new TenantSpendDto(
            tenantId,
            currentMonthStart,
            periodEnd,
            today,
            spentToDate,
            currentSoft,
            currentHard,
            BudgetForecastCalculator.ProjectPeriodSpend(spentToDate, today.Day, daysInPeriod),
            months,
            resetsThisPeriod.Count,
            resetsThisPeriod.Count == 0 ? null : resetsThisPeriod.Max(reset => reset.PerformedAt));
    }
}

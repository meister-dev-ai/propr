// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Budgeting;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Services;

namespace MeisterProPR.Infrastructure.Features.Budgeting;

/// <summary>
///     Composes a tenant's aggregate spend from its clients' configured caps and a single per-month cost rollup
///     across those clients, projecting the current-period aggregate with <see cref="BudgetForecastCalculator" />.
///     The caps reported are the sum of the clients' monthly caps (a reference total, since budgets are per client).
/// </summary>
public sealed class TenantBudgetSpendService(
    IClientAdminService clientAdminService,
    IClientTokenUsageRepository usageRepository,
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

        var softCaps = clients
            .Select(client => client.BudgetConfigOrEmpty.MonthlySoftCapUsd)
            .Where(cap => cap is not null)
            .Select(cap => cap!.Value)
            .ToList();
        var hardCaps = clients
            .Select(client => client.BudgetConfigOrEmpty.MonthlyHardCapUsd)
            .Where(cap => cap is not null)
            .Select(cap => cap!.Value)
            .ToList();

        var costByMonth = await usageRepository
            .GetMonthlyCostForClientsAsync(clientIds, firstMonthStart, today, ct)
            .ConfigureAwait(false);

        var months = new List<TenantSpendMonthDto>(clampedMonths);
        for (var offset = 0; offset < clampedMonths; offset++)
        {
            var monthStart = firstMonthStart.AddMonths(offset);
            costByMonth.TryGetValue((monthStart.Year, monthStart.Month), out var spent);
            months.Add(new TenantSpendMonthDto(monthStart.Year, monthStart.Month, monthStart, spent));
        }

        costByMonth.TryGetValue((today.Year, today.Month), out var spentToDate);

        return new TenantSpendDto(
            tenantId,
            currentMonthStart,
            periodEnd,
            today,
            spentToDate,
            softCaps.Count > 0 ? softCaps.Sum() : null,
            hardCaps.Count > 0 ? hardCaps.Sum() : null,
            BudgetForecastCalculator.ProjectPeriodSpend(spentToDate, today.Day, daysInPeriod),
            months);
    }
}

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
///     Composes a tenant-wide budget overview from the tenant's clients (with the caps in force for the current
///     period, manual-reset allowance included) and a single per-client cost rollup for the current calendar month,
///     projecting each client's full-period spend with <see cref="BudgetForecastCalculator" />. Rows are ordered by
///     spend-to-date descending so the highest spenders surface first.
/// </summary>
public sealed class TenantBudgetOverviewService(
    IClientAdminService clientAdminService,
    IClientTokenUsageRepository usageRepository,
    IBudgetSpendResetRepository resetRepository,
    TimeProvider timeProvider) : ITenantBudgetOverviewService
{
    /// <inheritdoc />
    public async Task<TenantBudgetOverviewDto> GetOverviewAsync(Guid tenantId, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var periodStart = new DateOnly(today.Year, today.Month, 1);
        var daysInPeriod = DateTime.DaysInMonth(today.Year, today.Month);
        var periodEnd = new DateOnly(today.Year, today.Month, daysInPeriod);

        var clients = (await clientAdminService.GetAllAsync(ct).ConfigureAwait(false))
            .Where(client => client.TenantId == tenantId)
            .ToList();

        // One query for every client's month-to-date cost, then joined in memory (no per-client round-trip).
        var costByClient = await usageRepository
            .GetCostByClientAndDateRangeAsync(periodStart, today, ct)
            .ConfigureAwait(false);

        // Likewise one query for this period's manual resets across the tenant's clients.
        var clientIds = clients.Select(client => client.Id).ToList();
        var resetsByClient = (await resetRepository
                .GetForClientsInRangeAsync(clientIds, periodStart, periodStart, ct)
                .ConfigureAwait(false))
            .GroupBy(reset => reset.ClientId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var rows = clients
            .Select(client =>
            {
                var caps = client.BudgetConfigOrEmpty;
                var spentToDate = costByClient.TryGetValue(client.Id, out var spent) ? spent : 0m;
                IReadOnlyList<BudgetSpendReset> resets =
                    resetsByClient.TryGetValue(client.Id, out var found) ? found : [];
                var topUp = MonthlyBudgetTopUp.SumTopUps(resets);
                return new TenantBudgetOverviewClientDto(
                    client.Id,
                    client.DisplayName,
                    spentToDate,
                    MonthlyBudgetTopUp.ApplyTo(caps.MonthlySoftCapUsd, topUp.SoftUsd),
                    MonthlyBudgetTopUp.ApplyTo(caps.MonthlyHardCapUsd, topUp.HardUsd),
                    BudgetForecastCalculator.ProjectPeriodSpend(spentToDate, today.Day, daysInPeriod),
                    resets.Count);
            })
            .OrderByDescending(row => row.SpentToDateUsd)
            .ThenBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new TenantBudgetOverviewDto(tenantId, periodStart, periodEnd, today, rows);
    }
}

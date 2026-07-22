// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Budgeting;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Services;

namespace MeisterProPR.Infrastructure.Features.Budgeting;

/// <summary>
///     Composes a tenant-wide budget overview from the tenant's clients (with their configured caps) and a single
///     per-client cost rollup for the current calendar month, projecting each client's full-period spend with
///     <see cref="BudgetForecastCalculator" />. Rows are ordered by spend-to-date descending so the highest
///     spenders surface first.
/// </summary>
public sealed class TenantBudgetOverviewService(
    IClientAdminService clientAdminService,
    IClientTokenUsageRepository usageRepository,
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

        var rows = clients
            .Select(client =>
            {
                var caps = client.BudgetConfigOrEmpty;
                var spentToDate = costByClient.TryGetValue(client.Id, out var spent) ? spent : 0m;
                return new TenantBudgetOverviewClientDto(
                    client.Id,
                    client.DisplayName,
                    spentToDate,
                    caps.MonthlySoftCapUsd,
                    caps.MonthlyHardCapUsd,
                    BudgetForecastCalculator.ProjectPeriodSpend(spentToDate, today.Day, daysInPeriod));
            })
            .OrderByDescending(row => row.SpentToDateUsd)
            .ThenBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new TenantBudgetOverviewDto(tenantId, periodStart, periodEnd, today, rows);
    }
}

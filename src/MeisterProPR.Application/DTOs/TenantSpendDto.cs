// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.DTOs;

/// <summary>Aggregated USD spend for a single calendar month across a tenant.</summary>
/// <param name="Year">The calendar year.</param>
/// <param name="Month">The calendar month (1-12).</param>
/// <param name="PeriodStart">Inclusive first day of the month (UTC).</param>
/// <param name="SpentUsd">Aggregate estimated USD spent across the tenant's clients that month (month-to-date for the current month).</param>
public sealed record TenantSpendMonthDto(
    int Year,
    int Month,
    DateOnly PeriodStart,
    decimal SpentUsd);

/// <summary>
///     Response DTO for <c>GET /admin/tenants/{tenantId}/budget/spend</c>: the tenant's aggregate USD spend for the
///     current monthly period plus a trailing per-month trend. Because budgets are configured per client, the caps
///     here are the SUM of the tenant's clients' monthly caps (a reference total, not a tenant-level cap); they are
///     null when no client in the tenant has that cap configured.
/// </summary>
/// <param name="TenantId">The tenant the spend belongs to.</param>
/// <param name="PeriodStart">Inclusive first day of the current monthly period (UTC).</param>
/// <param name="PeriodEnd">Inclusive last day of the current monthly period (UTC).</param>
/// <param name="AsOf">The UTC date spend was computed as of.</param>
/// <param name="SpentToDateUsd">Aggregate estimated USD spent across the tenant's clients this period to date.</param>
/// <param name="MonthlySoftCapUsd">Sum of the tenant's clients' monthly soft caps, or null when none are configured.</param>
/// <param name="MonthlyHardCapUsd">Sum of the tenant's clients' monthly hard caps, or null when none are configured.</param>
/// <param name="ProjectedPeriodSpendUsd">The projected full-period aggregate spend on the current run-rate.</param>
/// <param name="Months">Per-month aggregate spend over the trailing window, oldest first.</param>
public sealed record TenantSpendDto(
    Guid TenantId,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    DateOnly AsOf,
    decimal SpentToDateUsd,
    decimal? MonthlySoftCapUsd,
    decimal? MonthlyHardCapUsd,
    decimal? ProjectedPeriodSpendUsd,
    IReadOnlyList<TenantSpendMonthDto> Months);

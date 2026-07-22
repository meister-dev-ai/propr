// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.DTOs;

/// <summary>One client's current-period spend against its monthly budget, as a row in the tenant overview.</summary>
/// <param name="ClientId">The client identifier.</param>
/// <param name="DisplayName">The client's display name.</param>
/// <param name="SpentToDateUsd">Estimated USD spent this period to date (unpriced usage omitted).</param>
/// <param name="MonthlySoftCapUsd">The configured monthly soft cap, or null when unset (no limit).</param>
/// <param name="MonthlyHardCapUsd">The configured monthly hard cap, or null when unset (no limit).</param>
/// <param name="ProjectedPeriodSpendUsd">The projected full-period spend on the current run-rate.</param>
public sealed record TenantBudgetOverviewClientDto(
    Guid ClientId,
    string DisplayName,
    decimal SpentToDateUsd,
    decimal? MonthlySoftCapUsd,
    decimal? MonthlyHardCapUsd,
    decimal? ProjectedPeriodSpendUsd);

/// <summary>
///     Response DTO for <c>GET /admin/tenants/{tenantId}/budget/overview</c>: current-period spend against budget
///     for every client in a tenant, ordered by spend descending. The period is the current calendar month (UTC).
/// </summary>
/// <param name="TenantId">The tenant the overview belongs to.</param>
/// <param name="PeriodStart">Inclusive first day of the current monthly period (UTC).</param>
/// <param name="PeriodEnd">Inclusive last day of the current monthly period (UTC).</param>
/// <param name="AsOf">The UTC date spend was computed as of.</param>
/// <param name="Clients">One row per client in the tenant, ordered by spend-to-date descending.</param>
public sealed record TenantBudgetOverviewDto(
    Guid TenantId,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    DateOnly AsOf,
    IReadOnlyList<TenantBudgetOverviewClientDto> Clients);

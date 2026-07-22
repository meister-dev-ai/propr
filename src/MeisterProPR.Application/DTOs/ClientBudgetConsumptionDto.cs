// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.DTOs;

/// <summary>USD spend recorded on a single day of the reporting period.</summary>
/// <param name="Date">The UTC date.</param>
/// <param name="SpentUsd">Estimated USD spent on that date (unpriced usage is omitted, not coerced to zero).</param>
public sealed record BudgetDailySpendDto(DateOnly Date, decimal SpentUsd);

/// <summary>
///     Response DTO for <c>GET /admin/clients/{clientId}/budget/consumption</c>: the client's spend against its
///     monthly budget for the current period, plus a trajectory projection. The monthly period is calendar-based
///     (UTC) and resets each month, matching how the guardrail phase accumulates client-scope spend.
/// </summary>
/// <param name="ClientId">The client the consumption belongs to.</param>
/// <param name="PeriodStart">Inclusive first day of the current monthly period (UTC).</param>
/// <param name="PeriodEnd">Inclusive last day of the current monthly period (UTC).</param>
/// <param name="NextResetOn">The day the period resets to zero (first day of the next month, UTC).</param>
/// <param name="AsOf">The UTC date the spend was computed as of.</param>
/// <param name="SpentToDateUsd">Estimated USD spent in the period up to and including <paramref name="AsOf" />.</param>
/// <param name="SpendIsApproximate">True when some usage in the period lacked pricing, so the total is a lower bound.</param>
/// <param name="MonthlySoftCapUsd">The configured monthly soft cap, or null when unset (no limit).</param>
/// <param name="MonthlyHardCapUsd">The configured monthly hard cap, or null when unset (no limit).</param>
/// <param name="ProjectedPeriodSpendUsd">
///     The projected full-period spend on the current run-rate, or null when it cannot be projected (no elapsed days).
/// </param>
/// <param name="DailySpend">Per-day estimated spend across the period, ordered by date ascending.</param>
public sealed record ClientBudgetConsumptionDto(
    Guid ClientId,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    DateOnly NextResetOn,
    DateOnly AsOf,
    decimal SpentToDateUsd,
    bool SpendIsApproximate,
    decimal? MonthlySoftCapUsd,
    decimal? MonthlyHardCapUsd,
    decimal? ProjectedPeriodSpendUsd,
    IReadOnlyList<BudgetDailySpendDto> DailySpend);

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.DTOs;

/// <summary>Estimated USD spend for a single calendar month in a client's budget history.</summary>
/// <param name="Year">The calendar year.</param>
/// <param name="Month">The calendar month (1-12).</param>
/// <param name="PeriodStart">Inclusive first day of the month (UTC).</param>
/// <param name="SpentUsd">
///     Estimated USD spent in the month (month-to-date for the current, in-progress month; the full month for past
///     months). Unpriced usage is omitted, not coerced to zero.
/// </param>
/// <param name="SpendIsApproximate">True when some usage in the month lacked pricing, so the total is a lower bound.</param>
public sealed record BudgetMonthSpendDto(
    int Year,
    int Month,
    DateOnly PeriodStart,
    decimal SpentUsd,
    bool SpendIsApproximate);

/// <summary>
///     Response DTO for <c>GET /admin/clients/{clientId}/budget/history</c>: the client's estimated USD spend per
///     calendar month over a trailing window, plus the currently configured monthly caps for comparison. Because
///     caps are not snapshotted historically, the caps describe the current configuration, not the caps that were
///     in effect in each past month.
/// </summary>
/// <param name="ClientId">The client the history belongs to.</param>
/// <param name="MonthlySoftCapUsd">The currently configured monthly soft cap, or null when unset (no limit).</param>
/// <param name="MonthlyHardCapUsd">The currently configured monthly hard cap, or null when unset (no limit).</param>
/// <param name="Months">One entry per month in the window, oldest first (months with no spend are present with zero).</param>
public sealed record ClientBudgetHistoryDto(
    Guid ClientId,
    decimal? MonthlySoftCapUsd,
    decimal? MonthlyHardCapUsd,
    IReadOnlyList<BudgetMonthSpendDto> Months);

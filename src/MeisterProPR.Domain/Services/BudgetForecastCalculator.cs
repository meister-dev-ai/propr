// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterProPR.Domain.Services;

/// <summary>
///     Pure trajectory forecast for a budget period: projects the full-period spend from the month-to-date spend
///     on the current run-rate (spend-per-elapsed-day extrapolated across the whole period). This is a visibility
///     aid only — enforcement remains reactive to actual spend and never consults a forecast.
/// </summary>
public static class BudgetForecastCalculator
{
    /// <summary>
    ///     Projects the spend for the whole period from the spend accumulated so far.
    /// </summary>
    /// <param name="spentToDateUsd">USD spent in the period up to and including the current day.</param>
    /// <param name="elapsedDays">Days elapsed in the period so far (the current day counts as elapsed).</param>
    /// <param name="daysInPeriod">Total number of days in the period.</param>
    /// <returns>
    ///     The projected full-period spend, or <see langword="null" /> when it cannot be projected because
    ///     <paramref name="elapsedDays" /> or <paramref name="daysInPeriod" /> is not positive. The projection is
    ///     never below <paramref name="spentToDateUsd" /> (elapsed is clamped to the period length).
    /// </returns>
    public static decimal? ProjectPeriodSpend(decimal spentToDateUsd, int elapsedDays, int daysInPeriod)
    {
        if (elapsedDays <= 0 || daysInPeriod <= 0)
        {
            return null;
        }

        // Multiply before dividing so an exact period (elapsed == days) projects to exactly the spend so far,
        // without decimal division rounding.
        var effectiveElapsed = Math.Min(elapsedDays, daysInPeriod);
        return spentToDateUsd * daysInPeriod / effectiveElapsed;
    }
}

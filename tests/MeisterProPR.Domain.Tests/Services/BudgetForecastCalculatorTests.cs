// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Services;
using Xunit;

namespace MeisterProPR.Domain.Tests.Services;

public sealed class BudgetForecastCalculatorTests
{
    [Fact]
    public void ProjectPeriodSpend_ScalesMonthToDateSpendByTheRunRate()
    {
        // $50 spent over the first 10 of 30 days projects to $150 for the full period.
        Assert.Equal(150m, BudgetForecastCalculator.ProjectPeriodSpend(spentToDateUsd: 50m, elapsedDays: 10, daysInPeriod: 30));
    }

    [Fact]
    public void ProjectPeriodSpend_EqualsSpend_WhenThePeriodIsComplete()
    {
        Assert.Equal(50m, BudgetForecastCalculator.ProjectPeriodSpend(spentToDateUsd: 50m, elapsedDays: 30, daysInPeriod: 30));
    }

    [Fact]
    public void ProjectPeriodSpend_IsZero_WhenNothingHasBeenSpent()
    {
        Assert.Equal(0m, BudgetForecastCalculator.ProjectPeriodSpend(spentToDateUsd: 0m, elapsedDays: 5, daysInPeriod: 30));
    }

    [Fact]
    public void ProjectPeriodSpend_ClampsElapsedToThePeriodLength()
    {
        // Never projects below spend-to-date, even if elapsed somehow exceeds the period length.
        Assert.Equal(50m, BudgetForecastCalculator.ProjectPeriodSpend(spentToDateUsd: 50m, elapsedDays: 31, daysInPeriod: 30));
    }

    [Fact]
    public void ProjectPeriodSpend_ReturnsNull_WhenElapsedDaysIsNonPositive()
    {
        Assert.Null(BudgetForecastCalculator.ProjectPeriodSpend(spentToDateUsd: 50m, elapsedDays: 0, daysInPeriod: 30));
    }

    [Fact]
    public void ProjectPeriodSpend_ReturnsNull_WhenPeriodLengthIsNonPositive()
    {
        Assert.Null(BudgetForecastCalculator.ProjectPeriodSpend(spentToDateUsd: 50m, elapsedDays: 5, daysInPeriod: 0));
    }
}

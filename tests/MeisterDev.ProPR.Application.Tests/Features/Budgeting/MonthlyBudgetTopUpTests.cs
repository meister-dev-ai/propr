// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Budgeting;
using MeisterDev.ProPR.Application.Features.Budgeting.Models;
using MeisterDev.ProPR.Domain.Entities;
using Xunit;

namespace MeisterDev.ProPR.Application.Tests.Features.Budgeting;

public sealed class MonthlyBudgetTopUpTests
{
    private static readonly BudgetCaps Configured = new(
        MonthlySoftCapUsd: 80m,
        MonthlyHardCapUsd: 100m,
        PullRequestSoftCapUsd: 8m,
        PullRequestHardCapUsd: 10m,
        IncrementSoftCapUsd: 4m,
        IncrementHardCapUsd: 5m);

    private static BudgetSpendReset Reset(decimal? soft, decimal? hard) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = Guid.NewGuid(),
        PeriodStart = new DateOnly(2026, 7, 1),
        TopUpSoftCapUsd = soft,
        TopUpHardCapUsd = hard,
        PerformedAt = new DateTime(2026, 7, 15, 9, 14, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void SumTopUps_ReturnsNone_ForAPeriodWithoutResets()
    {
        Assert.Equal(MonthlyCapTopUp.None, MonthlyBudgetTopUp.SumTopUps([]));
        Assert.False(MonthlyBudgetTopUp.SumTopUps([]).Any);
    }

    [Fact]
    public void SumTopUps_AddsEveryGrantInThePeriod()
    {
        var topUp = MonthlyBudgetTopUp.SumTopUps([Reset(80m, 100m), Reset(80m, 100m)]);

        Assert.Equal(160m, topUp.SoftUsd);
        Assert.Equal(200m, topUp.HardUsd);
        Assert.True(topUp.Any);
    }

    [Fact]
    public void SumTopUps_TreatsAnUnconfiguredScopeAsNoGrant()
    {
        var topUp = MonthlyBudgetTopUp.SumTopUps([Reset(80m, null)]);

        Assert.Equal(80m, topUp.SoftUsd);
        Assert.Equal(0m, topUp.HardUsd);
    }

    [Fact]
    public void Apply_AddsTheGrantedAllowanceToTheConfiguredMonthlyCaps()
    {
        var effective = MonthlyBudgetTopUp.Apply(Configured, new MonthlyCapTopUp(80m, 100m));

        Assert.Equal(160m, effective.MonthlySoftCapUsd);
        Assert.Equal(200m, effective.MonthlyHardCapUsd);
    }

    [Fact]
    public void Apply_LeavesAnUnconfiguredCapUncapped()
    {
        var softOnly = Configured with { MonthlyHardCapUsd = null };

        var effective = MonthlyBudgetTopUp.Apply(softOnly, new MonthlyCapTopUp(80m, 100m));

        Assert.Equal(160m, effective.MonthlySoftCapUsd);
        Assert.Null(effective.MonthlyHardCapUsd);
    }

    [Fact]
    public void Apply_NeverTouchesThePullRequestOrIncrementScopes()
    {
        var effective = MonthlyBudgetTopUp.Apply(Configured, new MonthlyCapTopUp(80m, 100m));

        Assert.Equal(Configured.PullRequestSoftCapUsd, effective.PullRequestSoftCapUsd);
        Assert.Equal(Configured.PullRequestHardCapUsd, effective.PullRequestHardCapUsd);
        Assert.Equal(Configured.IncrementSoftCapUsd, effective.IncrementSoftCapUsd);
        Assert.Equal(Configured.IncrementHardCapUsd, effective.IncrementHardCapUsd);
    }

    [Fact]
    public void Apply_ReturnsTheConfiguredCapsUnchanged_WhenNothingWasGranted()
    {
        Assert.Equal(Configured, MonthlyBudgetTopUp.Apply(Configured, MonthlyCapTopUp.None));
    }

    [Fact]
    public void Apply_AddsTheFrozenGrantToWhateverCapIsCurrentlyConfigured()
    {
        // A reset granted $100 while the cap was $100; the cap was later raised to $150. The grant stays $100 — it
        // is snapshotted — while the baseline it is added to is always the cap configured today.
        var edited = Configured with { MonthlyHardCapUsd = 150m };

        var effective = MonthlyBudgetTopUp.Apply(edited, new MonthlyCapTopUp(0m, 100m));

        Assert.Equal(250m, effective.MonthlyHardCapUsd);
    }
}

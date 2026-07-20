// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Budgeting;
using MeisterProPR.Application.Features.Budgeting.Models;
using MeisterProPR.Domain.Enums;
using Xunit;

namespace MeisterProPR.Application.Tests.Features.Budgeting;

public sealed class BudgetEvaluatorTests
{
    private static readonly BudgetCaps Caps = new(
        MonthlySoftCapUsd: 80m,
        MonthlyHardCapUsd: 100m,
        PullRequestSoftCapUsd: 8m,
        PullRequestHardCapUsd: 10m,
        IncrementSoftCapUsd: 4m,
        IncrementHardCapUsd: 5m);

    [Fact]
    public void FindHardCapBreach_ReturnsNull_WhenEveryScopeIsUnderItsCap()
    {
        Assert.Null(BudgetEvaluator.FindHardCapBreach(Caps, clientSpentUsd: 50m, pullRequestSpentUsd: 5m, incrementSpentUsd: 2m));
    }

    [Fact]
    public void FindHardCapBreach_ReportsTheMostSpecificScope_WhenSeveralAreReached()
    {
        var breach = BudgetEvaluator.FindHardCapBreach(Caps, clientSpentUsd: 100m, pullRequestSpentUsd: 10m, incrementSpentUsd: 5m);

        Assert.NotNull(breach);
        Assert.Equal(BudgetScopeKind.Increment, breach!.Scope);
        Assert.Equal(BudgetCapKind.Hard, breach.CapKind);
        Assert.Equal(5m, breach.ThresholdUsd);
        Assert.Equal(5m, breach.SpentUsd);
    }

    [Fact]
    public void FindHardCapBreach_ReturnsClientScope_WhenOnlyTheClientCapIsReached()
    {
        var breach = BudgetEvaluator.FindHardCapBreach(Caps, clientSpentUsd: 120m, pullRequestSpentUsd: 5m, incrementSpentUsd: 2m);

        Assert.NotNull(breach);
        Assert.Equal(BudgetScopeKind.ClientMonthly, breach!.Scope);
        Assert.Equal(100m, breach.ThresholdUsd);
    }

    [Fact]
    public void FindSoftCapBreach_IgnoresTheIncrementScope()
    {
        // The admission soft-cap breach never considers the increment scope: its soft cap is an in-run stop,
        // evaluated separately by FindIncrementSoftCapBreach.
        var breach = BudgetEvaluator.FindSoftCapBreach(Caps, clientSpentUsd: 50m, pullRequestSpentUsd: 5m);
        Assert.Null(breach);
    }

    [Fact]
    public void FindIncrementSoftCapBreach_ReturnsIncrementSoftCap_WhenReached()
    {
        var breach = BudgetEvaluator.FindIncrementSoftCapBreach(Caps, incrementSpentUsd: 4m);

        Assert.NotNull(breach);
        Assert.Equal(BudgetScopeKind.Increment, breach!.Scope);
        Assert.Equal(BudgetCapKind.Soft, breach.CapKind);
        Assert.Equal(4m, breach.ThresholdUsd);
        Assert.Equal(4m, breach.SpentUsd);
    }

    [Fact]
    public void FindIncrementSoftCapBreach_ReturnsNull_WhenUnderCapOrUnconfigured()
    {
        Assert.Null(BudgetEvaluator.FindIncrementSoftCapBreach(Caps, incrementSpentUsd: 3.99m));
        Assert.Null(BudgetEvaluator.FindIncrementSoftCapBreach(BudgetCaps.None, incrementSpentUsd: 1_000m));
    }

    [Fact]
    public void FindAdmissionBreach_NeverReportsTheIncrementSoftCap()
    {
        // A brand-new job has no increment spend, and the increment soft cap is an in-run stop — so it must not
        // gate admission even when the (hypothetical) increment spend is over the soft cap.
        var breach = BudgetEvaluator.FindAdmissionBreach(Caps, clientSpentUsd: 10m, pullRequestSpentUsd: 1m, incrementSpentUsd: 4m);
        Assert.Null(breach);
    }

    [Fact]
    public void FindSoftCapBreach_ReturnsThePullRequestSoftCap_WhenReached()
    {
        var breach = BudgetEvaluator.FindSoftCapBreach(Caps, clientSpentUsd: 50m, pullRequestSpentUsd: 8m);

        Assert.NotNull(breach);
        Assert.Equal(BudgetScopeKind.PullRequest, breach!.Scope);
        Assert.Equal(BudgetCapKind.Soft, breach.CapKind);
    }

    [Fact]
    public void FindAdmissionBreach_PrefersAReachedHardCapOverASoftCap()
    {
        var breach = BudgetEvaluator.FindAdmissionBreach(Caps, clientSpentUsd: 100m, pullRequestSpentUsd: 5m, incrementSpentUsd: 2m);

        Assert.NotNull(breach);
        Assert.Equal(BudgetCapKind.Hard, breach!.CapKind);
        Assert.Equal(BudgetScopeKind.ClientMonthly, breach.Scope);
    }

    [Fact]
    public void FindAdmissionBreach_FallsBackToASoftCap_WhenNoHardCapIsReached()
    {
        var breach = BudgetEvaluator.FindAdmissionBreach(Caps, clientSpentUsd: 80m, pullRequestSpentUsd: 5m, incrementSpentUsd: 2m);

        Assert.NotNull(breach);
        Assert.Equal(BudgetCapKind.Soft, breach!.CapKind);
        Assert.Equal(BudgetScopeKind.ClientMonthly, breach.Scope);
    }

    [Fact]
    public void FindHardCapBreach_ReturnsNull_WhenNoCapsAreConfigured()
    {
        Assert.Null(BudgetEvaluator.FindHardCapBreach(BudgetCaps.None, 1_000m, 1_000m, 1_000m));
    }
}

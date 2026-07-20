// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Budgeting;
using MeisterProPR.Application.Features.Budgeting.Models;
using MeisterProPR.Domain.Enums;
using Xunit;

namespace MeisterProPR.Application.Tests.Features.Budgeting;

public sealed class BudgetScopeTests
{
    [Fact]
    public void ThrowIfHardCapReached_DoesNotThrow_WhileBaselinePlusRunningIsUnderTheCap()
    {
        var scope = MakeScope(incrementHardCapUsd: 10m, incrementBaselineUsd: 4m);
        scope.RecordCall(5m); // effective increment spend 4 + 5 = 9 < 10

        scope.ThrowIfHardCapReached();
        Assert.Null(scope.TrippedBreach);
    }

    [Fact]
    public void ThrowIfHardCapReached_Throws_WhenBaselinePlusRunningReachesTheCap()
    {
        var scope = MakeScope(incrementHardCapUsd: 10m, incrementBaselineUsd: 4m);
        scope.RecordCall(6m); // effective increment spend 4 + 6 = 10 >= 10

        var exception = Assert.Throws<BudgetHardCapReachedException>(scope.ThrowIfHardCapReached);
        Assert.Equal(BudgetScopeKind.Increment, exception.Breach.Scope);
        Assert.Equal(10m, exception.Breach.ThresholdUsd);
        Assert.Equal(10m, exception.Breach.SpentUsd);

        // The trip is recorded so a wrapped surfacing is still recognizable as a budget cut.
        Assert.NotNull(scope.TrippedBreach);
        Assert.Equal(BudgetScopeKind.Increment, scope.TrippedBreach!.Scope);
    }

    [Fact]
    public void RecordCall_WithNullCost_FlagsApproximate_WithoutAdvancingTheRunningTotal()
    {
        var scope = MakeScope(incrementHardCapUsd: 10m, incrementBaselineUsd: 0m);
        scope.RecordCall(3m);
        scope.RecordCall(null);

        Assert.Equal(3m, scope.RunningUsd);
        Assert.True(scope.RunningIsApproximate);
    }

    [Fact]
    public void ThrowIfHardCapReached_IsANoOp_WhenNoHardCapIsConfigured()
    {
        var scope = new BudgetScope(
            new BudgetCaps(
                MonthlySoftCapUsd: 5m, MonthlyHardCapUsd: null, PullRequestSoftCapUsd: null, PullRequestHardCapUsd: null, IncrementSoftCapUsd: null,
                IncrementHardCapUsd: null),
            new ReviewSpendBaseline(new ReviewScopeSpend(1_000m, false), ReviewScopeSpend.None, ReviewScopeSpend.None));

        scope.ThrowIfHardCapReached();
        Assert.Null(scope.TrippedBreach);
    }

    [Fact]
    public void IsIncrementSoftCapReached_IsFalse_WhileBaselinePlusRunningIsUnderTheCap()
    {
        var scope = MakeScope(incrementHardCapUsd: null, incrementBaselineUsd: 2m, incrementSoftCapUsd: 8m);
        scope.RecordCall(3m); // effective increment spend 2 + 3 = 5 < 8

        Assert.False(scope.IsIncrementSoftCapReached());
        Assert.Null(scope.IncrementSoftCapBreach);
    }

    [Fact]
    public void IsIncrementSoftCapReached_IsTrueAndRecordsBreach_WhenReached()
    {
        var scope = MakeScope(incrementHardCapUsd: null, incrementBaselineUsd: 2m, incrementSoftCapUsd: 8m);
        scope.RecordCall(6m); // effective increment spend 2 + 6 = 8 >= 8

        Assert.True(scope.IsIncrementSoftCapReached());
        Assert.NotNull(scope.IncrementSoftCapBreach);
        Assert.Equal(BudgetScopeKind.Increment, scope.IncrementSoftCapBreach!.Scope);
        Assert.Equal(BudgetCapKind.Soft, scope.IncrementSoftCapBreach.CapKind);
        Assert.Equal(8m, scope.IncrementSoftCapBreach.ThresholdUsd);
    }

    [Fact]
    public void IsIncrementSoftCapReached_IsFalse_WhenNoIncrementSoftCapIsConfigured()
    {
        var scope = MakeScope(incrementHardCapUsd: 10m, incrementBaselineUsd: 0m, incrementSoftCapUsd: null);
        scope.RecordCall(9m);

        Assert.False(scope.IsIncrementSoftCapReached());
        Assert.Null(scope.IncrementSoftCapBreach);
    }

    private static BudgetScope MakeScope(decimal? incrementHardCapUsd, decimal incrementBaselineUsd, decimal? incrementSoftCapUsd = null)
    {
        var caps = new BudgetCaps(
            MonthlySoftCapUsd: null,
            MonthlyHardCapUsd: null,
            PullRequestSoftCapUsd: null,
            PullRequestHardCapUsd: null,
            IncrementSoftCapUsd: incrementSoftCapUsd,
            IncrementHardCapUsd: incrementHardCapUsd);
        var baseline = new ReviewSpendBaseline(
            ReviewScopeSpend.None,
            ReviewScopeSpend.None,
            new ReviewScopeSpend(incrementBaselineUsd, false));
        return new BudgetScope(caps, baseline);
    }
}

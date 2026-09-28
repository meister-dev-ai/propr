// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Budgeting;
using MeisterDev.ProPR.Application.Features.Budgeting.Models;
using MeisterDev.ProPR.Domain.Enums;
using Xunit;

namespace MeisterDev.ProPR.Application.Tests.Features.Budgeting;

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
        var breach = exception.Breach;
        Assert.NotNull(breach);
        Assert.Equal(BudgetScopeKind.Increment, breach.Scope);
        Assert.Equal(10m, breach.ThresholdUsd);
        Assert.Equal(10m, breach.SpentUsd);

        // The trip is recorded so a wrapped surfacing is still recognizable as a budget cut.
        var tripped = scope.TrippedBreach;
        Assert.NotNull(tripped);
        Assert.Equal(BudgetScopeKind.Increment, tripped.Scope);
    }

    // A refusal relayed from the control plane names the condition without carrying the cap, and a caller
    // holding no scope of its own has nothing else to name it by. The cap stays unknown, so no scope,
    // threshold or spend is invented for the job that is about to record the stop.
    [Fact]
    public void ResolveBreach_WithNoCapOnEitherSide_LeavesTheCapUnknown()
    {
        Assert.Null(new BudgetHardCapReachedException(null).ResolveBreach(null));
    }

    [Fact]
    public void ResolveBreach_WithACapTheCallersScopeTripped_NamesThatCap()
    {
        var tripped = new BudgetBreach(BudgetScopeKind.PullRequest, BudgetCapKind.Hard, 4m, 4.5m);

        Assert.Same(tripped, new BudgetHardCapReachedException(null).ResolveBreach(tripped));
    }

    // The cap the refusal named outranks the caller's, because it is the one the call was refused against.
    [Fact]
    public void ResolveBreach_WithACapOnTheRefusal_KeepsIt()
    {
        var refused = new BudgetBreach(BudgetScopeKind.Increment, BudgetCapKind.Hard, 5m, 6m);
        var tripped = new BudgetBreach(BudgetScopeKind.PullRequest, BudgetCapKind.Hard, 4m, 4.5m);

        Assert.Same(refused, new BudgetHardCapReachedException(refused).ResolveBreach(tripped));
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
            new ReviewSpendBaseline(new ReviewScopeSpend(1_000m, false), ReviewScopeSpend.None, ReviewScopeSpend.None, ReviewScopeSpend.None));

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

    [Fact]
    public void ThrowIfHardCapReached_CutsTheRun_WhenTheTenantHardCapIsReached()
    {
        var scope = new BudgetScope(
            new BudgetCaps(
                MonthlySoftCapUsd: null, MonthlyHardCapUsd: 10_000m, PullRequestSoftCapUsd: null, PullRequestHardCapUsd: null,
                IncrementSoftCapUsd: null, IncrementHardCapUsd: null, TenantMonthlySoftCapUsd: null, TenantMonthlyHardCapUsd: 5_000m),
            new ReviewSpendBaseline(
                new ReviewScopeSpend(10m, false),
                ReviewScopeSpend.None,
                ReviewScopeSpend.None,
                new ReviewScopeSpend(4_999m, false)));

        scope.RecordCall(1m);

        var ex = Assert.Throws<BudgetHardCapReachedException>(scope.ThrowIfHardCapReached);
        Assert.Equal(BudgetScopeKind.TenantMonthly, ex.Breach.Scope);
        Assert.Equal(BudgetCapKind.Hard, ex.Breach.CapKind);
        Assert.Equal(BudgetScopeKind.TenantMonthly, scope.TrippedBreach!.Scope);
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
            new ReviewScopeSpend(incrementBaselineUsd, false), ReviewScopeSpend.None);
        return new BudgetScope(caps, baseline);
    }
}

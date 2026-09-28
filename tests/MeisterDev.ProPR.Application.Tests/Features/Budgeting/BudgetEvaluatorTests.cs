// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Budgeting;
using MeisterDev.ProPR.Application.Features.Budgeting.Models;
using MeisterDev.ProPR.Domain.Enums;
using Xunit;

namespace MeisterDev.ProPR.Application.Tests.Features.Budgeting;

public sealed class BudgetEvaluatorTests
{
    private static readonly BudgetCaps Caps = new(
        MonthlySoftCapUsd: 80m,
        MonthlyHardCapUsd: 100m,
        PullRequestSoftCapUsd: 8m,
        PullRequestHardCapUsd: 10m,
        IncrementSoftCapUsd: 4m,
        IncrementHardCapUsd: 5m);

    private static readonly BudgetCaps TenantCaps = Caps with
    {
        TenantMonthlySoftCapUsd = 4_000m,
        TenantMonthlyHardCapUsd = 5_000m,
    };

    [Fact]
    public void FindHardCapBreach_ReturnsNull_WhenEveryScopeIsUnderItsCap()
    {
        Assert.Null(BudgetEvaluator.FindHardCapBreach(Caps, clientSpentUsd: 50m, pullRequestSpentUsd: 5m, incrementSpentUsd: 2m, tenantSpentUsd: 0m));
    }

    [Fact]
    public void FindHardCapBreach_ReportsTheMostSpecificScope_WhenSeveralAreReached()
    {
        var breach = BudgetEvaluator.FindHardCapBreach(Caps, clientSpentUsd: 100m, pullRequestSpentUsd: 10m, incrementSpentUsd: 5m, tenantSpentUsd: 0m);

        Assert.NotNull(breach);
        Assert.Equal(BudgetScopeKind.Increment, breach!.Scope);
        Assert.Equal(BudgetCapKind.Hard, breach.CapKind);
        Assert.Equal(5m, breach.ThresholdUsd);
        Assert.Equal(5m, breach.SpentUsd);
    }

    [Fact]
    public void FindHardCapBreach_ReturnsClientScope_WhenOnlyTheClientCapIsReached()
    {
        var breach = BudgetEvaluator.FindHardCapBreach(Caps, clientSpentUsd: 120m, pullRequestSpentUsd: 5m, incrementSpentUsd: 2m, tenantSpentUsd: 0m);

        Assert.NotNull(breach);
        Assert.Equal(BudgetScopeKind.ClientMonthly, breach!.Scope);
        Assert.Equal(100m, breach.ThresholdUsd);
    }

    [Fact]
    public void FindSoftCapBreach_IgnoresTheIncrementScope()
    {
        // The admission soft-cap breach never considers the increment scope: its soft cap is an in-run stop,
        // evaluated separately by FindIncrementSoftCapBreach.
        var breach = BudgetEvaluator.FindSoftCapBreach(Caps, clientSpentUsd: 50m, pullRequestSpentUsd: 5m, tenantSpentUsd: 0m);
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
        var breach = BudgetEvaluator.FindAdmissionBreach(Caps, clientSpentUsd: 10m, pullRequestSpentUsd: 1m, incrementSpentUsd: 4m, tenantSpentUsd: 0m);
        Assert.Null(breach);
    }

    [Fact]
    public void FindSoftCapBreach_ReturnsThePullRequestSoftCap_WhenReached()
    {
        var breach = BudgetEvaluator.FindSoftCapBreach(Caps, clientSpentUsd: 50m, pullRequestSpentUsd: 8m, tenantSpentUsd: 0m);

        Assert.NotNull(breach);
        Assert.Equal(BudgetScopeKind.PullRequest, breach!.Scope);
        Assert.Equal(BudgetCapKind.Soft, breach.CapKind);
    }

    [Fact]
    public void FindAdmissionBreach_PrefersAReachedHardCapOverASoftCap()
    {
        var breach = BudgetEvaluator.FindAdmissionBreach(Caps, clientSpentUsd: 100m, pullRequestSpentUsd: 5m, incrementSpentUsd: 2m, tenantSpentUsd: 0m);

        Assert.NotNull(breach);
        Assert.Equal(BudgetCapKind.Hard, breach!.CapKind);
        Assert.Equal(BudgetScopeKind.ClientMonthly, breach.Scope);
    }

    [Fact]
    public void FindAdmissionBreach_FallsBackToASoftCap_WhenNoHardCapIsReached()
    {
        var breach = BudgetEvaluator.FindAdmissionBreach(Caps, clientSpentUsd: 80m, pullRequestSpentUsd: 5m, incrementSpentUsd: 2m, tenantSpentUsd: 0m);

        Assert.NotNull(breach);
        Assert.Equal(BudgetCapKind.Soft, breach!.CapKind);
        Assert.Equal(BudgetScopeKind.ClientMonthly, breach.Scope);
    }

    [Fact]
    public void FindHardCapBreach_ReturnsNull_WhenNoCapsAreConfigured()
    {
        Assert.Null(BudgetEvaluator.FindHardCapBreach(BudgetCaps.None, 1_000m, 1_000m, 1_000m, tenantSpentUsd: 0m));
    }

    [Fact]
    public void FindHardCapBreach_ReturnsTheTenantScope_WhenOnlyTheTenantCapIsReached()
    {
        var breach = BudgetEvaluator.FindHardCapBreach(TenantCaps, clientSpentUsd: 50m, pullRequestSpentUsd: 5m, incrementSpentUsd: 2m, tenantSpentUsd: 5_000m);

        Assert.NotNull(breach);
        Assert.Equal(BudgetScopeKind.TenantMonthly, breach!.Scope);
        Assert.Equal(BudgetCapKind.Hard, breach.CapKind);
        Assert.Equal(5_000m, breach.ThresholdUsd);
        Assert.Equal(5_000m, breach.SpentUsd);
    }

    [Fact]
    public void FindHardCapBreach_ReportsTheClientScope_WhenTheClientCapIsReachedTogetherWithTheTenantCap()
    {
        // The tenant scope is the widest one, so a client cap that is also reached is the one an operator has
        // to act on.
        var breach = BudgetEvaluator.FindHardCapBreach(
            TenantCaps, clientSpentUsd: 100m, pullRequestSpentUsd: 5m, incrementSpentUsd: 2m, tenantSpentUsd: 5_000m);

        Assert.NotNull(breach);
        Assert.Equal(BudgetScopeKind.ClientMonthly, breach!.Scope);
    }

    [Fact]
    public void FindSoftCapBreach_ReturnsTheTenantScope_WhenOnlyTheTenantSoftCapIsReached()
    {
        // The spend is past the cap, so the reported spend is what was observed and not the threshold.
        var breach = BudgetEvaluator.FindSoftCapBreach(TenantCaps, clientSpentUsd: 50m, pullRequestSpentUsd: 5m, tenantSpentUsd: 4_001m);

        Assert.NotNull(breach);
        Assert.Equal(BudgetScopeKind.TenantMonthly, breach!.Scope);
        Assert.Equal(BudgetCapKind.Soft, breach.CapKind);
        Assert.Equal(4_000m, breach.ThresholdUsd);
        Assert.Equal(4_001m, breach.SpentUsd);
    }

    [Fact]
    public void FindAdmissionBreach_HoldsOnTheTenantSoftCap_WhenNoClientCapIsReached()
    {
        var breach = BudgetEvaluator.FindAdmissionBreach(
            TenantCaps, clientSpentUsd: 10m, pullRequestSpentUsd: 1m, incrementSpentUsd: 0m, tenantSpentUsd: 4_001m);

        Assert.NotNull(breach);
        Assert.Equal(BudgetScopeKind.TenantMonthly, breach!.Scope);
        Assert.Equal(BudgetCapKind.Soft, breach.CapKind);
        Assert.Equal(4_000m, breach.ThresholdUsd);
        Assert.Equal(4_001m, breach.SpentUsd);
    }

    [Fact]
    public void FindAdmissionBreach_PrefersTheTenantHardCapOverTheTenantSoftCap()
    {
        var breach = BudgetEvaluator.FindAdmissionBreach(
            TenantCaps, clientSpentUsd: 10m, pullRequestSpentUsd: 1m, incrementSpentUsd: 0m, tenantSpentUsd: 5_001m);

        Assert.NotNull(breach);
        Assert.Equal(BudgetScopeKind.TenantMonthly, breach!.Scope);
        Assert.Equal(BudgetCapKind.Hard, breach.CapKind);
        Assert.Equal(5_000m, breach.ThresholdUsd);
        Assert.Equal(5_001m, breach.SpentUsd);
    }

    [Fact]
    public void FindHardCapBreach_IgnoresTheTenantSpend_WhenTheTenantHasNoCap()
    {
        Assert.Null(BudgetEvaluator.FindHardCapBreach(Caps, clientSpentUsd: 50m, pullRequestSpentUsd: 5m, incrementSpentUsd: 2m, tenantSpentUsd: 1_000_000m));
    }
}

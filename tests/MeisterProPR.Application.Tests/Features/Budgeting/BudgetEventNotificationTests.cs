// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Budgeting.Models;
using MeisterProPR.Domain.Enums;
using Xunit;

namespace MeisterProPR.Application.Tests.Features.Budgeting;

public sealed class BudgetEventNotificationTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid JobId = Guid.NewGuid();

    [Fact]
    public void FromBreach_MapsAHardCapBreachToAHardCapReachedEvent()
    {
        var breach = new BudgetBreach(BudgetScopeKind.ClientMonthly, BudgetCapKind.Hard, 100m, 105m);

        var notification = BudgetEventNotification.FromBreach(breach, ClientId, JobId, pullRequestId: 42, iterationId: 3);

        Assert.Equal(BudgetEventType.HardCapReached, notification.EventType);
        Assert.Equal(BudgetScopeKind.ClientMonthly, notification.Scope);
        Assert.Equal(100m, notification.ThresholdUsd);
        Assert.Equal(105m, notification.SpentUsd);
        Assert.Equal(ClientId, notification.ClientId);
        Assert.Equal(JobId, notification.JobId);
        Assert.Equal(42, notification.PullRequestId);
        Assert.Equal(3, notification.IterationId);
    }

    [Fact]
    public void FromBreach_MapsASoftCapBreachToASoftCapReachedEvent()
    {
        var breach = new BudgetBreach(BudgetScopeKind.Increment, BudgetCapKind.Soft, 4m, 4m);

        var notification = BudgetEventNotification.FromBreach(breach, ClientId, JobId, pullRequestId: 7, iterationId: 1);

        Assert.Equal(BudgetEventType.SoftCapReached, notification.EventType);
        Assert.Equal(BudgetScopeKind.Increment, notification.Scope);
    }
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Budgeting.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.Budgeting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace MeisterProPR.Infrastructure.Tests.Features.Budgeting;

public sealed class BudgetEventPublisherTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);

    private readonly IBudgetEventRepository _repository = Substitute.For<IBudgetEventRepository>();

    private BudgetEventPublisher CreatePublisher() =>
        new(this._repository, new FixedTimeProvider(FixedNow), NullLogger<BudgetEventPublisher>.Instance);

    [Fact]
    public async Task PublishAsync_PersistsAnEventCarryingTheNotificationDetails()
    {
        BudgetEvent? saved = null;
        await this._repository.AddAsync(Arg.Do<BudgetEvent>(e => saved = e), Arg.Any<CancellationToken>());
        var clientId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        var publisher = this.CreatePublisher();
        await publisher.PublishAsync(
            new BudgetEventNotification(
                clientId,
                BudgetEventType.HardCapReached,
                BudgetScopeKind.PullRequest,
                10m,
                12m,
                jobId,
                PullRequestId: 42,
                IterationId: 5));

        Assert.NotNull(saved);
        Assert.NotEqual(Guid.Empty, saved!.Id);
        Assert.Equal(clientId, saved.ClientId);
        Assert.Equal(BudgetEventType.HardCapReached, saved.EventType);
        Assert.Equal(BudgetScopeKind.PullRequest, saved.Scope);
        Assert.Equal(10m, saved.ThresholdUsd);
        Assert.Equal(12m, saved.SpentUsd);
        Assert.Equal(jobId, saved.JobId);
        Assert.Equal(42, saved.PullRequestId);
        Assert.Equal(5, saved.IterationId);
        Assert.Equal(FixedNow.UtcDateTime, saved.OccurredAt);
        Assert.Equal(DateTimeKind.Utc, saved.OccurredAt.Kind);
    }

    [Fact]
    public async Task PublishAsync_SwallowsRepositoryFailures_SoEmissionNeverBreaksTheReview()
    {
        this._repository
            .AddAsync(Arg.Any<BudgetEvent>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("db down"));

        var publisher = this.CreatePublisher();

        // Must not throw.
        await publisher.PublishAsync(
            new BudgetEventNotification(
                Guid.NewGuid(),
                BudgetEventType.SoftCapReached,
                BudgetScopeKind.ClientMonthly,
                80m,
                80m));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

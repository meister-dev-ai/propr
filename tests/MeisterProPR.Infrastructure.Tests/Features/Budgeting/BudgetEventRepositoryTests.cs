// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Features.Budgeting;
using MeisterProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterProPR.Infrastructure.Tests.Features.Budgeting;

/// <summary>Integration tests for <see cref="BudgetEventRepository" /> against a real PostgreSQL instance.</summary>
[Collection("PostgresIntegration")]
public sealed class BudgetEventRepositoryTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private MeisterProPRDbContext _dbContext = null!;
    private BudgetEventRepository _repository = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);

        await this._dbContext.BudgetEvents.ExecuteDeleteAsync();
        this._repository = new BudgetEventRepository(new TestDbContextFactory(options));
    }

    public async Task DisposeAsync()
    {
        if (this._dbContext is null)
        {
            return;
        }

        await this._dbContext.BudgetEvents.ExecuteDeleteAsync();
        await this._dbContext.DisposeAsync();
    }

    [Fact]
    public async Task GetByClientSinceAsync_ReturnsOnlyTheClientsEventsAtOrAfterTheCutoff_InOrder()
    {
        var clientId = Guid.NewGuid();
        var otherClient = Guid.NewGuid();
        var cutoff = new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc);

        await this._repository.AddAsync(Event(clientId, new DateTime(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc)), default); // before cutoff
        await this._repository.AddAsync(Event(clientId, new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc)), default);
        await this._repository.AddAsync(Event(clientId, new DateTime(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc)), default);
        await this._repository.AddAsync(Event(otherClient, new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc)), default); // other client

        var events = await this._repository.GetByClientSinceAsync(clientId, cutoff, default);

        Assert.Equal(2, events.Count);
        Assert.Equal(new DateTime(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc), events[0].OccurredAt);
        Assert.Equal(new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc), events[1].OccurredAt);
        Assert.All(events, e => Assert.Equal(clientId, e.ClientId));
    }

    private static BudgetEvent Event(Guid clientId, DateTime occurredAt) =>
        new()
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            EventType = BudgetEventType.SoftCapReached,
            Scope = BudgetScopeKind.ClientMonthly,
            ThresholdUsd = 80m,
            SpentUsd = 82m,
            JobId = Guid.NewGuid(),
            PullRequestId = 42,
            IterationId = 1,
            OccurredAt = occurredAt,
        };
}

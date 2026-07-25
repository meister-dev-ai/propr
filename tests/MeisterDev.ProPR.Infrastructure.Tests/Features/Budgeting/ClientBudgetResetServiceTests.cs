// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.Budgeting;
using MeisterDev.ProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Budgeting;

/// <summary>
///     Unit tests for <see cref="ClientBudgetResetService" />: the granted allowance, the snapshotted before/after
///     audit values, stacking within a period, and the cases where a reset is refused.
/// </summary>
public sealed class ClientBudgetResetServiceTests : IDisposable
{
    private static readonly DateTimeOffset MidJuly = new(2026, 7, 15, 9, 14, 0, TimeSpan.Zero);

    private readonly MeisterProPRDbContext _dbContext;
    private readonly TestDbContextFactory _factory;
    private readonly BudgetSpendResetRepository _resetRepository;

    public ClientBudgetResetServiceTests()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"ClientBudgetResetServiceTests-{Guid.NewGuid():N}")
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);
        this._factory = new TestDbContextFactory(options);
        this._resetRepository = new BudgetSpendResetRepository(this._factory);
    }

    public void Dispose() => this._dbContext.Dispose();

    [Fact]
    public async Task ResetAsync_GrantsTheConfiguredCapAgain_AndRecordsTheCapsBeforeAndAfter()
    {
        var clientId = await this.GivenClientAsync(soft: 80m, hard: 100m);
        var service = this.CreateService();

        var result = await service.ResetAsync(clientId, actorUserId: Guid.NewGuid());

        Assert.Equal(BudgetSpendResetOutcome.Applied, result.Outcome);
        Assert.NotNull(result.Reset);
        var reset = result.Reset;
        Assert.Equal(new DateOnly(2026, 7, 1), reset.PeriodStart);
        Assert.Equal(80m, reset.TopUpSoftCapUsd);
        Assert.Equal(100m, reset.TopUpHardCapUsd);
        Assert.Equal(80m, reset.EffectiveSoftCapBeforeUsd);
        Assert.Equal(160m, reset.EffectiveSoftCapAfterUsd);
        Assert.Equal(100m, reset.EffectiveHardCapBeforeUsd);
        Assert.Equal(200m, reset.EffectiveHardCapAfterUsd);
        Assert.Equal(MidJuly.UtcDateTime, reset.PerformedAt);
    }

    [Fact]
    public async Task ResetAsync_PersistsTheRowWithItsActor()
    {
        var clientId = await this.GivenClientAsync(soft: null, hard: 100m);
        var actor = Guid.NewGuid();
        var service = this.CreateService();

        await service.ResetAsync(clientId, actor);

        var stored = await this._resetRepository.GetByClientAndPeriodAsync(
            clientId,
            new DateOnly(2026, 7, 1),
            CancellationToken.None);
        Assert.Equal(actor, Assert.Single(stored).ActorUserId);
    }

    [Fact]
    public async Task ResetAsync_StacksOnTopOfAnEarlierResetInTheSamePeriod()
    {
        var clientId = await this.GivenClientAsync(soft: 80m, hard: 100m);
        var service = this.CreateService();

        await service.ResetAsync(clientId, actorUserId: null);
        var second = await service.ResetAsync(clientId, actorUserId: null);

        Assert.Equal(200m, second.Reset!.EffectiveHardCapBeforeUsd);
        Assert.Equal(300m, second.Reset.EffectiveHardCapAfterUsd);
    }

    [Fact]
    public async Task ResetAsync_LeavesAnUnconfiguredScopeUncapped()
    {
        var clientId = await this.GivenClientAsync(soft: null, hard: 100m);
        var service = this.CreateService();

        var result = await service.ResetAsync(clientId, actorUserId: null);

        Assert.Null(result.Reset!.TopUpSoftCapUsd);
        Assert.Null(result.Reset.EffectiveSoftCapBeforeUsd);
        Assert.Null(result.Reset.EffectiveSoftCapAfterUsd);
        Assert.Equal(200m, result.Reset.EffectiveHardCapAfterUsd);
    }

    [Fact]
    public async Task ResetAsync_RefusesAndWritesNothing_WhenNoMonthlyCapIsConfigured()
    {
        var clientId = await this.GivenClientAsync(soft: null, hard: null);
        var service = this.CreateService();

        var result = await service.ResetAsync(clientId, actorUserId: null);

        Assert.Equal(BudgetSpendResetOutcome.NoMonthlyCapConfigured, result.Outcome);
        Assert.Null(result.Reset);
        Assert.Empty(
            await this._resetRepository.GetByClientAndPeriodAsync(
                clientId,
                new DateOnly(2026, 7, 1),
                CancellationToken.None));
    }

    [Fact]
    public async Task ResetAsync_RefusesAndWritesNothing_WhenEveryConfiguredCapIsZero()
    {
        // A $0 cap is a legal "block everything" setting; topping it up would grant nothing but still stamp the
        // period as reset and report success.
        var clientId = await this.GivenClientAsync(soft: 0m, hard: 0m);
        var service = this.CreateService();

        var result = await service.ResetAsync(clientId, actorUserId: null);

        Assert.Equal(BudgetSpendResetOutcome.NoMonthlyCapConfigured, result.Outcome);
        Assert.Empty(
            await this._resetRepository.GetByClientAndPeriodAsync(
                clientId,
                new DateOnly(2026, 7, 1),
                CancellationToken.None));
    }

    [Fact]
    public async Task ResetAsync_StillGrants_WhenOnlyOneScopeIsZero()
    {
        var clientId = await this.GivenClientAsync(soft: 0m, hard: 100m);
        var service = this.CreateService();

        var result = await service.ResetAsync(clientId, actorUserId: null);

        Assert.Equal(BudgetSpendResetOutcome.Applied, result.Outcome);
        Assert.Equal(200m, result.Reset!.EffectiveHardCapAfterUsd);
    }

    [Fact]
    public async Task ResetAsync_RefusesAndWritesNothing_WhenTheClientIsUnknown()
    {
        var unknown = Guid.NewGuid();
        var service = this.CreateService();

        var result = await service.ResetAsync(unknown, actorUserId: null);

        Assert.Equal(BudgetSpendResetOutcome.ClientNotFound, result.Outcome);
        Assert.Null(result.Reset);
        Assert.Empty(
            await this._resetRepository.GetByClientAndPeriodAsync(
                unknown,
                new DateOnly(2026, 7, 1),
                CancellationToken.None));
    }

    private ClientBudgetResetService CreateService() =>
        new(this._factory, this._resetRepository, new FixedTimeProvider(MidJuly));

    private async Task<Guid> GivenClientAsync(decimal? soft, decimal? hard)
    {
        var clientId = Guid.NewGuid();
        this._dbContext.Clients.Add(
            new ClientRecord
            {
                Id = clientId,
                DisplayName = "Budget Reset Test Client",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
                MonthlyBudgetSoftCapUsd = soft,
                MonthlyBudgetHardCapUsd = hard,
            });
        await this._dbContext.SaveChangesAsync();
        return clientId;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Budgeting;
using MeisterDev.ProPR.Application.Features.Budgeting.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Features.Budgeting;
using NSubstitute;
using Xunit;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Budgeting;

/// <summary>
///     Unit tests for <see cref="ClientBudgetConsumptionService" />: period math, cap composition, the
///     approximate flag, daily grouping, and the trajectory projection — over mocked caps and usage.
/// </summary>
public sealed class ClientBudgetConsumptionServiceTests
{
    private static readonly Guid ClientId = Guid.NewGuid();

    private readonly IBudgetCapsProvider _capsProvider = Substitute.For<IBudgetCapsProvider>();
    private readonly IClientTokenUsageRepository _usageRepository = Substitute.For<IClientTokenUsageRepository>();

    private readonly IBudgetSpendResetRepository _resetRepository = Substitute.For<IBudgetSpendResetRepository>();
    private readonly IUserRepository _userRepository = Substitute.For<IUserRepository>();

    private ClientBudgetConsumptionService CreateService(DateTimeOffset now)
    {
        return new ClientBudgetConsumptionService(
            this._capsProvider,
            this._usageRepository,
            this._resetRepository,
            this._userRepository,
            new FixedTimeProvider(now));
    }

    /// <summary>Stubs every cap read the service can make, so a test's caps hold whichever period it asks about.</summary>
    private void GivenCaps(BudgetCaps caps)
    {
        this._capsProvider.GetCapsAsync(ClientId, Arg.Any<CancellationToken>()).Returns(caps);
        this._capsProvider.GetConfiguredCapsAsync(ClientId, Arg.Any<CancellationToken>()).Returns(caps);
    }

    private void GivenResets(params BudgetSpendReset[] resets)
    {
        this._resetRepository
            .GetByClientAndPeriodAsync(ClientId, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(resets);
        this._resetRepository
            .GetForClientsInRangeAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(),
                Arg.Any<DateOnly>(),
                Arg.Any<DateOnly>(),
                Arg.Any<CancellationToken>())
            .Returns(resets);
    }

    private void GivenSamples(params ClientTokenUsageSample[] samples) =>
        this._usageRepository
            .GetByClientAndDateRangeAsync(ClientId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(samples);

    [Fact]
    public async Task GetConsumptionAsync_ReportsSpendCapsPeriodAndProjection_ForTheCurrentMonth()
    {
        this.GivenCaps(new BudgetCaps(80m, 100m, null, null, null, null));
        this.GivenSamples(
            Sample(new DateOnly(2026, 7, 1), 20m),
            Sample(new DateOnly(2026, 7, 10), 30m));

        var service = this.CreateService(new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero));
        var result = await service.GetConsumptionAsync(ClientId);

        Assert.Equal(new DateOnly(2026, 7, 1), result.PeriodStart);
        Assert.Equal(new DateOnly(2026, 7, 31), result.PeriodEnd);
        Assert.Equal(new DateOnly(2026, 8, 1), result.NextResetOn);
        Assert.Equal(new DateOnly(2026, 7, 10), result.AsOf);
        Assert.Equal(50m, result.SpentToDateUsd);
        Assert.False(result.SpendIsApproximate);
        Assert.Equal(80m, result.MonthlySoftCapUsd);
        Assert.Equal(100m, result.MonthlyHardCapUsd);
        // 50 spent over 10 of 31 days projects to 155.
        Assert.Equal(50m / 10 * 31, result.ProjectedPeriodSpendUsd);
        Assert.Equal(
            new[] { new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 10) },
            result.DailySpend.Select(d => d.Date));
    }

    [Fact]
    public async Task GetConsumptionAsync_FlagsApproximate_AndOmitsUnpricedUsageFromTheTotal()
    {
        this.GivenCaps(BudgetCaps.None);
        this.GivenSamples(
            Sample(new DateOnly(2026, 7, 5), 12m),
            Sample(new DateOnly(2026, 7, 6), null));

        var service = this.CreateService(new DateTimeOffset(2026, 7, 6, 0, 0, 0, TimeSpan.Zero));
        var result = await service.GetConsumptionAsync(ClientId);

        Assert.Equal(12m, result.SpentToDateUsd);
        Assert.True(result.SpendIsApproximate);
        Assert.Null(result.MonthlySoftCapUsd);
        Assert.Null(result.MonthlyHardCapUsd);
    }

    [Fact]
    public async Task GetConsumptionAsync_SumsMultipleModelsIntoOneDailyPoint()
    {
        this.GivenCaps(BudgetCaps.None);
        this.GivenSamples(
            Sample(new DateOnly(2026, 7, 4), 3m, "gpt-4o"),
            Sample(new DateOnly(2026, 7, 4), 2m, "text-embedding-3-small"));

        var service = this.CreateService(new DateTimeOffset(2026, 7, 4, 0, 0, 0, TimeSpan.Zero));
        var result = await service.GetConsumptionAsync(ClientId);

        var day = Assert.Single(result.DailySpend);
        Assert.Equal(new DateOnly(2026, 7, 4), day.Date);
        Assert.Equal(5m, day.SpentUsd);
        Assert.Equal(5m, result.SpentToDateUsd);
    }

    [Fact]
    public async Task GetConsumptionAsync_ReturnsZeroSpendAndProjection_WhenNoUsageThisPeriod()
    {
        this.GivenCaps(new BudgetCaps(50m, null, null, null, null, null));
        this.GivenSamples();

        var service = this.CreateService(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        var result = await service.GetConsumptionAsync(ClientId);

        Assert.Equal(0m, result.SpentToDateUsd);
        Assert.False(result.SpendIsApproximate);
        Assert.Empty(result.DailySpend);
        Assert.Equal(0m, result.ProjectedPeriodSpendUsd);
    }

    [Fact]
    public async Task GetConsumptionAsync_ForAPastMonth_ReturnsFullMonthActualsWithNoForecast()
    {
        this.GivenCaps(new BudgetCaps(80m, 100m, null, null, null, null));
        // The mock returns these regardless of range; they represent June's samples for the June query.
        this.GivenSamples(
            Sample(new DateOnly(2026, 6, 3), 15m),
            Sample(new DateOnly(2026, 6, 28), 20m));

        // "Now" is mid-July; request the previous (complete) month.
        var service = this.CreateService(new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero));
        var result = await service.GetConsumptionAsync(ClientId, 2026, 6);

        Assert.Equal(new DateOnly(2026, 6, 1), result.PeriodStart);
        Assert.Equal(new DateOnly(2026, 6, 30), result.PeriodEnd);
        // A past month is measured across the whole (already-complete) month.
        Assert.Equal(new DateOnly(2026, 6, 30), result.AsOf);
        Assert.Equal(35m, result.SpentToDateUsd);
        // Caps still reflect the current configuration; no forecast for a complete month.
        Assert.Equal(100m, result.MonthlyHardCapUsd);
        Assert.Null(result.ProjectedPeriodSpendUsd);
    }

    [Fact]
    public async Task GetConsumptionAsync_DoesNotOverflow_ForTheMaxRepresentableMonth()
    {
        // A far-future period passed directly to the API (year 9999, month 12) has no next-month date; the service
        // must clamp rather than overflow DateOnly.
        this.GivenCaps(BudgetCaps.None);

        var service = this.CreateService(new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero));
        var result = await service.GetConsumptionAsync(ClientId, 9999, 12);

        Assert.Equal(new DateOnly(9999, 12, 1), result.PeriodStart);
        Assert.Equal(new DateOnly(9999, 12, 31), result.PeriodEnd);
        Assert.Equal(new DateOnly(9999, 12, 31), result.NextResetOn);
        Assert.Equal(0m, result.SpentToDateUsd);
        Assert.Null(result.ProjectedPeriodSpendUsd);
    }

    [Fact]
    public async Task GetHistoryAsync_ReturnsOneEntryPerMonth_ZeroFillingMonthsWithoutSpend()
    {
        this.GivenCaps(new BudgetCaps(80m, 100m, null, null, null, null));
        // May has spend, June none, July (current) has month-to-date spend.
        this.GivenSamples(
            Sample(new DateOnly(2026, 5, 10), 12m),
            Sample(new DateOnly(2026, 7, 2), 8m));

        var service = this.CreateService(new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero));
        var history = await service.GetHistoryAsync(ClientId, monthsBack: 3);

        Assert.Equal(100m, history.MonthlyHardCapUsd);
        Assert.Equal(3, history.Months.Count);
        Assert.Equal((2026, 5, 12m), (history.Months[0].Year, history.Months[0].Month, history.Months[0].SpentUsd));
        Assert.Equal((2026, 6, 0m), (history.Months[1].Year, history.Months[1].Month, history.Months[1].SpentUsd));
        Assert.Equal((2026, 7, 8m), (history.Months[2].Year, history.Months[2].Month, history.Months[2].SpentUsd));
    }

    [Fact]
    public async Task GetConsumptionAsync_ComposesCapsFromTheRequestedPeriodsResets_NotTheCurrentOnes()
    {
        this.GivenCaps(new BudgetCaps(80m, 100m, null, null, null, null));
        this.GivenResets(ResetRow(new DateOnly(2026, 6, 1), Guid.NewGuid()));

        var service = this.CreateService(new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero));
        await service.GetConsumptionAsync(ClientId, 2026, 6);

        // The resets asked for are June's, because June is the period under report.
        await this._resetRepository.Received().GetByClientAndPeriodAsync(
            ClientId,
            new DateOnly(2026, 6, 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetConsumptionAsync_ReportsBothTheCapInForceAndTheConfiguredBaseline()
    {
        this.GivenCaps(new BudgetCaps(80m, 100m, null, null, null, null));
        this.GivenResets(ResetRow(new DateOnly(2026, 7, 1), Guid.NewGuid()));

        var service = this.CreateService(new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero));
        var result = await service.GetConsumptionAsync(ClientId);

        // The meter reads against the cumulative cap; the configured pair says what a further reset would grant.
        Assert.Equal(160m, result.MonthlySoftCapUsd);
        Assert.Equal(200m, result.MonthlyHardCapUsd);
        Assert.Equal(80m, result.ConfiguredSoftCapUsd);
        Assert.Equal(100m, result.ConfiguredHardCapUsd);
    }

    [Fact]
    public async Task GetConsumptionAsync_ReportsThePeriodsResetsWithTheActorsName()
    {
        this.GivenCaps(new BudgetCaps(80m, 100m, null, null, null, null));
        var actor = Guid.NewGuid();
        this.GivenResets(ResetRow(new DateOnly(2026, 7, 1), actor));
        this._userRepository.GetByIdAsync(actor, Arg.Any<CancellationToken>()).Returns(new AppUser { Id = actor, Username = "saen" });

        var service = this.CreateService(new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero));
        var result = await service.GetConsumptionAsync(ClientId);

        var reset = Assert.Single(result.Resets);
        Assert.Equal("saen", reset.ActorUsername);
        Assert.Equal(actor, reset.ActorUserId);
        Assert.Equal(100m, reset.EffectiveHardCapBeforeUsd);
        Assert.Equal(200m, reset.EffectiveHardCapAfterUsd);
        // The meter reads against the cumulative cap the provider resolved.
        Assert.Equal(200m, result.MonthlyHardCapUsd);
    }

    [Fact]
    public async Task GetConsumptionAsync_ReportsNoResets_ForAPeriodThatWasNeverReset()
    {
        this.GivenCaps(new BudgetCaps(80m, 100m, null, null, null, null));
        this.GivenResets();

        var service = this.CreateService(new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero));
        var result = await service.GetConsumptionAsync(ClientId);

        Assert.Empty(result.Resets);
    }

    [Fact]
    public async Task GetHistoryAsync_RaisesOnlyTheCapOfTheMonthThatWasReset()
    {
        // The configured caps are the baseline; only July received an extra $100/$100 allowance. The current-period
        // caps are stubbed HIGHER so this fails if the history ever composes from them instead of the baseline.
        this.GivenCaps(new BudgetCaps(80m, 100m, null, null, null, null));
        this._capsProvider
            .GetCapsAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(new BudgetCaps(999m, 999m, null, null, null, null));
        this.GivenResets(ResetRow(new DateOnly(2026, 7, 1), Guid.NewGuid()));

        var service = this.CreateService(new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero));
        var history = await service.GetHistoryAsync(ClientId, monthsBack: 3);

        // The window queried is the one charted: May through the current month, not some wider range.
        await this._resetRepository.Received().GetForClientsInRangeAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == ClientId),
            new DateOnly(2026, 5, 1),
            new DateOnly(2026, 7, 1),
            Arg.Any<CancellationToken>());
        // The top-level pair stays the configured baseline.
        Assert.Equal(100m, history.MonthlyHardCapUsd);
        Assert.Equal(100m, history.Months[1].EffectiveHardCapUsd);
        Assert.Equal(0, history.Months[1].ResetCount);
        Assert.Equal(200m, history.Months[2].EffectiveHardCapUsd);
        Assert.Equal(160m, history.Months[2].EffectiveSoftCapUsd);
        Assert.Equal(1, history.Months[2].ResetCount);
    }

    private static BudgetSpendReset ResetRow(DateOnly periodStart, Guid actorUserId) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = ClientId,
        PeriodStart = periodStart,
        TopUpSoftCapUsd = 80m,
        TopUpHardCapUsd = 100m,
        EffectiveSoftCapBeforeUsd = 80m,
        EffectiveSoftCapAfterUsd = 160m,
        EffectiveHardCapBeforeUsd = 100m,
        EffectiveHardCapAfterUsd = 200m,
        ActorUserId = actorUserId,
        PerformedAt = new DateTime(2026, 7, 15, 9, 14, 0, DateTimeKind.Utc),
    };

    private static ClientTokenUsageSample Sample(DateOnly date, decimal? costUsd, string modelId = "gpt-4o") =>
        new()
        {
            Id = Guid.NewGuid(),
            ClientId = ClientId,
            ModelId = modelId,
            Date = date,
            InputTokens = 100,
            OutputTokens = 50,
            EstimatedCostUsd = costUsd,
        };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

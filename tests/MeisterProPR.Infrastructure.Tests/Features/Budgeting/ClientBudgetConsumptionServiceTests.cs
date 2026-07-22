// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Budgeting;
using MeisterProPR.Application.Features.Budgeting.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Features.Budgeting;
using NSubstitute;
using Xunit;

namespace MeisterProPR.Infrastructure.Tests.Features.Budgeting;

/// <summary>
///     Unit tests for <see cref="ClientBudgetConsumptionService" />: period math, cap composition, the
///     approximate flag, daily grouping, and the trajectory projection — over mocked caps and usage.
/// </summary>
public sealed class ClientBudgetConsumptionServiceTests
{
    private static readonly Guid ClientId = Guid.NewGuid();

    private readonly IBudgetCapsProvider _capsProvider = Substitute.For<IBudgetCapsProvider>();
    private readonly IClientTokenUsageRepository _usageRepository = Substitute.For<IClientTokenUsageRepository>();

    private ClientBudgetConsumptionService CreateService(DateTimeOffset now)
    {
        return new ClientBudgetConsumptionService(this._capsProvider, this._usageRepository, new FixedTimeProvider(now));
    }

    private void GivenCaps(BudgetCaps caps) =>
        this._capsProvider.GetCapsAsync(ClientId, Arg.Any<CancellationToken>()).Returns(caps);

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

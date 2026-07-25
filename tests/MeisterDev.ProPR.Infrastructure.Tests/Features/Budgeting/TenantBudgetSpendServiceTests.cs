// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Features.Budgeting;
using NSubstitute;
using Xunit;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Budgeting;

/// <summary>
///     Unit tests for <see cref="TenantBudgetSpendService" />: aggregate spend, summed caps, projection, and the
///     zero-filled per-month trend, over mocked clients and a monthly cost rollup.
/// </summary>
public sealed class TenantBudgetSpendServiceTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    private readonly IClientAdminService _clientAdmin = Substitute.For<IClientAdminService>();
    private readonly IClientTokenUsageRepository _usageRepository = Substitute.For<IClientTokenUsageRepository>();

    private readonly IBudgetSpendResetRepository _resetRepository = Substitute.For<IBudgetSpendResetRepository>();

    private TenantBudgetSpendService CreateService(DateTimeOffset now) =>
        new(this._clientAdmin, this._usageRepository, this._resetRepository, new FixedTimeProvider(now));

    private void GivenResets(params BudgetSpendReset[] resets) =>
        this._resetRepository
            .GetForClientsInRangeAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(),
                Arg.Any<DateOnly>(),
                Arg.Any<DateOnly>(),
                Arg.Any<CancellationToken>())
            .Returns(resets);

    [Fact]
    public async Task GetSpendAsync_SumsCaps_AggregatesCurrentSpend_AndZeroFillsTheTrend()
    {
        this._clientAdmin.GetAllAsync(Arg.Any<CancellationToken>()).Returns(
            new List<ClientDto>
            {
                MakeClient("Acme", TenantId, soft: 80m, hard: 100m),
                MakeClient("Globex", TenantId, soft: 40m, hard: 50m),
                MakeClient("Other", Guid.NewGuid(), soft: 999m, hard: 999m),
            });
        this._usageRepository
            .GetMonthlyCostForClientsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<(int, int), decimal> { [(2026, 7)] = 100m, [(2026, 6)] = 90m });

        var service = this.CreateService(new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero));
        var result = await service.GetSpendAsync(TenantId, monthsBack: 3);

        Assert.Equal(TenantId, result.TenantId);
        // Caps summed across the tenant's clients only.
        Assert.Equal(120m, result.MonthlySoftCapUsd);
        Assert.Equal(150m, result.MonthlyHardCapUsd);
        // Current-month bucket drives spend-to-date + projection.
        Assert.Equal(100m, result.SpentToDateUsd);
        Assert.NotNull(result.ProjectedPeriodSpendUsd);
        // Trend: May (zero-filled), June, July.
        Assert.Equal(3, result.Months.Count);
        Assert.Equal((2026, 5, 0m), (result.Months[0].Year, result.Months[0].Month, result.Months[0].SpentUsd));
        Assert.Equal((2026, 6, 90m), (result.Months[1].Year, result.Months[1].Month, result.Months[1].SpentUsd));
        Assert.Equal((2026, 7, 100m), (result.Months[2].Year, result.Months[2].Month, result.Months[2].SpentUsd));
    }

    [Fact]
    public async Task GetSpendAsync_ReturnsNullCaps_WhenNoClientHasCapsConfigured()
    {
        this._clientAdmin.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<ClientDto> { MakeClient("Uncapped", TenantId, soft: null, hard: null) });
        this._usageRepository
            .GetMonthlyCostForClientsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<(int, int), decimal>());

        var service = this.CreateService(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        var result = await service.GetSpendAsync(TenantId, monthsBack: 12);

        Assert.Null(result.MonthlySoftCapUsd);
        Assert.Null(result.MonthlyHardCapUsd);
        Assert.Equal(0m, result.SpentToDateUsd);
    }

    [Fact]
    public async Task GetSpendAsync_SumsTheCapsInForce_AndCountsThePeriodsResets()
    {
        var acme = MakeClient("Acme", TenantId, soft: 80m, hard: 100m);
        var globex = MakeClient("Globex", TenantId, soft: 40m, hard: 50m);
        this._clientAdmin.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<ClientDto> { acme, globex });
        this._usageRepository
            .GetMonthlyCostForClientsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<(int, int), decimal> { [(2026, 7)] = 100m });
        // Acme was reset once this period: +80 soft / +100 hard.
        this.GivenResets(
            new BudgetSpendReset
            {
                Id = Guid.NewGuid(),
                ClientId = acme.Id,
                PeriodStart = new DateOnly(2026, 7, 1),
                TopUpSoftCapUsd = 80m,
                TopUpHardCapUsd = 100m,
                PerformedAt = new DateTime(2026, 7, 15, 9, 14, 0, DateTimeKind.Utc),
            });

        var service = this.CreateService(new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero));
        var spend = await service.GetSpendAsync(TenantId, monthsBack: 2);

        // 160 (Acme, topped up) + 40 (Globex) soft; 200 + 50 hard.
        Assert.Equal(200m, spend.MonthlySoftCapUsd);
        Assert.Equal(250m, spend.MonthlyHardCapUsd);
        Assert.Equal(1, spend.ResetCount);
    }

    private static ClientDto MakeClient(string name, Guid tenantId, decimal? soft, decimal? hard) =>
        new(
            Guid.NewGuid(),
            name,
            IsActive: true,
            CreatedAt: DateTimeOffset.UnixEpoch,
            CommentResolutionBehavior: default,
            CustomSystemMessage: null,
            DefaultReviewPipelineProfileId: null,
            DefaultReviewPipelineProfileUpdatedAtUtc: null,
            ScmCommentPostingEnabled: true,
            TenantId: tenantId,
            BudgetConfig: new BudgetConfigDto(MonthlySoftCapUsd: soft, MonthlyHardCapUsd: hard));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Features.Budgeting;
using NSubstitute;
using Xunit;

namespace MeisterProPR.Infrastructure.Tests.Features.Budgeting;

/// <summary>
///     Unit tests for <see cref="TenantBudgetSpendService" />: aggregate spend, summed caps, projection, and the
///     zero-filled per-month trend, over mocked clients and a monthly cost rollup.
/// </summary>
public sealed class TenantBudgetSpendServiceTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    private readonly IClientAdminService _clientAdmin = Substitute.For<IClientAdminService>();
    private readonly IClientTokenUsageRepository _usageRepository = Substitute.For<IClientTokenUsageRepository>();

    private TenantBudgetSpendService CreateService(DateTimeOffset now) =>
        new(this._clientAdmin, this._usageRepository, new FixedTimeProvider(now));

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

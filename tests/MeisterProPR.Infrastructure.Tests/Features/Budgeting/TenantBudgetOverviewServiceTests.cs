// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.Budgeting;
using NSubstitute;
using Xunit;

namespace MeisterProPR.Infrastructure.Tests.Features.Budgeting;

/// <summary>
///     Unit tests for <see cref="TenantBudgetOverviewService" />: tenant filtering, per-client spend join, ordering,
///     and missing-spend handling — over mocked clients and cost rollup.
/// </summary>
public sealed class TenantBudgetOverviewServiceTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    private readonly IClientAdminService _clientAdmin = Substitute.For<IClientAdminService>();
    private readonly IClientTokenUsageRepository _usageRepository = Substitute.For<IClientTokenUsageRepository>();

    private TenantBudgetOverviewService CreateService(DateTimeOffset now) =>
        new(this._clientAdmin, this._usageRepository, new FixedTimeProvider(now));

    [Fact]
    public async Task GetOverviewAsync_FiltersToTenant_JoinsSpend_AndOrdersBySpendDescending()
    {
        var acme = Guid.NewGuid();
        var globex = Guid.NewGuid();
        var otherTenantClient = Guid.NewGuid();
        this._clientAdmin.GetAllAsync(Arg.Any<CancellationToken>()).Returns(
            new List<ClientDto>
            {
                MakeClient(acme, "Acme", TenantId, soft: 80m, hard: 100m),
                MakeClient(globex, "Globex", TenantId, soft: null, hard: null),
                MakeClient(otherTenantClient, "Other", Guid.NewGuid(), soft: 50m, hard: 60m),
            });
        this._usageRepository
            .GetCostByClientAndDateRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, decimal> { [acme] = 30m, [globex] = 70m, [otherTenantClient] = 999m });

        var service = this.CreateService(new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero));
        var result = await service.GetOverviewAsync(TenantId);

        Assert.Equal(TenantId, result.TenantId);
        Assert.Equal(new DateOnly(2026, 7, 1), result.PeriodStart);
        Assert.Equal(new DateOnly(2026, 7, 10), result.AsOf);
        // The other tenant's client is excluded.
        Assert.Equal(2, result.Clients.Count);
        // Ordered by spend descending: Globex (70) before Acme (30).
        Assert.Equal("Globex", result.Clients[0].DisplayName);
        Assert.Equal(70m, result.Clients[0].SpentToDateUsd);
        Assert.Null(result.Clients[0].MonthlySoftCapUsd);
        Assert.Equal("Acme", result.Clients[1].DisplayName);
        Assert.Equal(30m, result.Clients[1].SpentToDateUsd);
        Assert.Equal(100m, result.Clients[1].MonthlyHardCapUsd);
        Assert.NotNull(result.Clients[1].ProjectedPeriodSpendUsd);
    }

    [Fact]
    public async Task GetOverviewAsync_DefaultsMissingSpendToZero()
    {
        var client = Guid.NewGuid();
        this._clientAdmin.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<ClientDto> { MakeClient(client, "Quiet", TenantId, soft: 80m, hard: 100m) });
        this._usageRepository
            .GetCostByClientAndDateRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, decimal>());

        var service = this.CreateService(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        var result = await service.GetOverviewAsync(TenantId);

        Assert.Equal(0m, Assert.Single(result.Clients).SpentToDateUsd);
    }

    private static ClientDto MakeClient(Guid id, string name, Guid tenantId, decimal? soft, decimal? hard) =>
        new(
            id,
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

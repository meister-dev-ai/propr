// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Workers;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Workers;

public sealed class ProCursorTokenUsageRollupWorkerTests
{
    private static ProCursorTokenUsageRollupWorker BuildWorker(
        IServiceScopeFactory scopeFactory,
        int pollSeconds = 1)
    {
        return new ProCursorTokenUsageRollupWorker(
            scopeFactory,
            Options.Create(new ProCursorTokenUsageOptions { RollupPollSeconds = pollSeconds }),
            NullLogger<ProCursorTokenUsageRollupWorker>.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_RefreshesRecentRollupsAndRunsRetention()
    {
        var aggregationService = Substitute.For<IProCursorTokenUsageAggregationService>();
        aggregationService.RefreshRecentAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(4));

        var retentionService = Substitute.For<IProCursorTokenUsageRetentionService>();
        retentionService.PurgeExpiredAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProCursorTokenUsageRetentionResult(2, 1, DateTimeOffset.UtcNow)));

        var scopeFactory = CreateScopeFactory(aggregationService, retentionService);
        var worker = BuildWorker(scopeFactory);

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);
        await Task.Delay(200, CancellationToken.None);

        Assert.True(worker.IsRunning);
        await aggregationService.Received().RefreshRecentAsync(Arg.Any<CancellationToken>());
        await retentionService.Received(1).PurgeExpiredAsync(Arg.Any<CancellationToken>());
        Assert.NotNull(worker.LastCycleCompletedAt);
        Assert.NotNull(worker.LastRetentionRunAtUtc);

        cts.Cancel();
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_WhenRefreshThrows_WorkerStaysRunning()
    {
        var aggregationService = Substitute.For<IProCursorTokenUsageAggregationService>();
        aggregationService.RefreshRecentAsync(Arg.Any<CancellationToken>())
            .Returns<Task<int>>(_ => throw new InvalidOperationException("rollup failed"));

        var retentionService = Substitute.For<IProCursorTokenUsageRetentionService>();
        var scopeFactory = CreateScopeFactory(aggregationService, retentionService);
        var worker = BuildWorker(scopeFactory);

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);
        await Task.Delay(200, CancellationToken.None);

        Assert.True(worker.IsRunning);

        cts.Cancel();
        await worker.StopAsync(CancellationToken.None);
    }

    private static IServiceScopeFactory CreateScopeFactory(
        IProCursorTokenUsageAggregationService aggregationService,
        IProCursorTokenUsageRetentionService retentionService)
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();

        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(serviceProvider);
        serviceProvider.GetService(typeof(IProCursorTokenUsageAggregationService)).Returns(aggregationService);
        serviceProvider.GetService(typeof(IProCursorTokenUsageRetentionService)).Returns(retentionService);

        return scopeFactory;
    }
}

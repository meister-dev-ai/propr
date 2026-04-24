// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Workers;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Application.Features.Licensing.Ports;
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
        var refreshInvoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        aggregationService.RefreshRecentAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                refreshInvoked.TrySetResult();
                return Task.FromResult(4);
            });

        var retentionService = Substitute.For<IProCursorTokenUsageRetentionService>();
        retentionService.PurgeExpiredAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProCursorTokenUsageRetentionResult(2, 1, DateTimeOffset.UtcNow)));

        var scopeFactory = CreateScopeFactory(aggregationService, retentionService);
        var worker = BuildWorker(scopeFactory);

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);
        await refreshInvoked.Task.WaitAsync(TimeSpan.FromSeconds(1));

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
        var refreshAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        aggregationService.RefreshRecentAsync(Arg.Any<CancellationToken>())
            .Returns<Task<int>>(_ =>
            {
                refreshAttempted.TrySetResult();
                throw new InvalidOperationException("rollup failed");
            });

        var retentionService = Substitute.For<IProCursorTokenUsageRetentionService>();
        var scopeFactory = CreateScopeFactory(aggregationService, retentionService);
        var worker = BuildWorker(scopeFactory);

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);
        await refreshAttempted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(worker.IsRunning);

        cts.Cancel();
        await worker.StopAsync(CancellationToken.None);
    }

    private static IServiceScopeFactory CreateScopeFactory(
        IProCursorTokenUsageAggregationService aggregationService,
        IProCursorTokenUsageRetentionService retentionService,
        ILicensingCapabilityService? licensingCapabilityService = null)
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        var resolvedLicensingService = licensingCapabilityService ?? CreateLicensingService(isAvailable: true);

        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(serviceProvider);
        serviceProvider.GetService(typeof(IProCursorTokenUsageAggregationService)).Returns(aggregationService);
        serviceProvider.GetService(typeof(IProCursorTokenUsageRetentionService)).Returns(retentionService);
        serviceProvider.GetService(typeof(ILicensingCapabilityService)).Returns(resolvedLicensingService);

        return scopeFactory;
    }

    private static ILicensingCapabilityService CreateLicensingService(bool isAvailable)
    {
        var licensingService = Substitute.For<ILicensingCapabilityService>();
        licensingService.GetCapabilityAsync(PremiumCapabilityKey.ProCursor, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CapabilitySnapshot(
                PremiumCapabilityKey.ProCursor,
                PremiumCapabilityKey.ProCursor,
                true,
                true,
                PremiumCapabilityOverrideState.Default,
                isAvailable,
                isAvailable ? null : "ProCursor requires a premium license.")));
        return licensingService;
    }

    [Fact]
    public async Task ExecuteAsync_WhenCapabilityUnavailable_SkipsRefreshAndRetention()
    {
        var aggregationService = Substitute.For<IProCursorTokenUsageAggregationService>();
        var retentionService = Substitute.For<IProCursorTokenUsageRetentionService>();
        var scopeFactory = CreateScopeFactory(
            aggregationService,
            retentionService,
            CreateLicensingService(isAvailable: false));
        var worker = BuildWorker(scopeFactory);

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(30, CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        await aggregationService.DidNotReceive().RefreshRecentAsync(Arg.Any<CancellationToken>());
        await retentionService.DidNotReceive().PurgeExpiredAsync(Arg.Any<CancellationToken>());
        Assert.NotNull(worker.LastCycleCompletedAt);
    }
}

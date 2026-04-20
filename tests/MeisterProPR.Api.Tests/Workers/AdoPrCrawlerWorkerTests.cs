// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Telemetry;
using MeisterProPR.Api.Workers;
using MeisterProPR.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Workers;

/// <summary>Unit tests for <see cref="AdoPrCrawlerWorker" />.</summary>
public sealed class AdoPrCrawlerWorkerTests
{
    private static AdoPrCrawlerWorker BuildWorker(
        IServiceScopeFactory scopeFactory,
        int intervalSeconds = 10)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["PR_CRAWL_INTERVAL_SECONDS"] = intervalSeconds.ToString(),
                })
            .Build();

        var metricsScope = Substitute.For<IServiceScope>();
        metricsScope.ServiceProvider.GetService(typeof(IJobRepository))
            .Returns(Substitute.For<IJobRepository>());

        var metricsScopeFactory = Substitute.For<IServiceScopeFactory>();
        metricsScopeFactory.CreateScope().Returns(metricsScope);

        var metrics = new ReviewJobMetrics(metricsScopeFactory);
        return new AdoPrCrawlerWorker(
            scopeFactory,
            metrics,
            config,
            NullLogger<AdoPrCrawlerWorker>.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_CallsCrawlService_OnEachTick()
    {
        // Arrange
        var fakePrCrawlService = Substitute.For<IPrCrawlService>();
        var crawlInvoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        fakePrCrawlService
            .CrawlAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                crawlInvoked.TrySetResult();
                return Task.CompletedTask;
            });

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.GetService(typeof(IPrCrawlService)).Returns(fakePrCrawlService);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var worker = BuildWorker(scopeFactory);

        // Act: start and let it run briefly then cancel
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        try
        {
            await worker.StartAsync(cts.Token);
            await crawlInvoked.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        // Assert: CrawlAsync was called at least once
        await fakePrCrawlService.Received().CrawlAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_CancellationToken_StopsWorker()
    {
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.GetService(typeof(IPrCrawlService))
            .Returns(Substitute.For<IPrCrawlService>());

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var worker = BuildWorker(scopeFactory, 60);
        using var cts = new CancellationTokenSource();

        await worker.StartAsync(cts.Token);
        cts.Cancel();

        // StopAsync should complete without throwing
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_ExceptionInCrawlService_WorkerDoesNotCrash()
    {
        // Arrange: PrCrawlService throws; worker must handle and continue
        var scope = Substitute.For<IServiceScope>();
        var crawlAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scope.ServiceProvider
            .GetService(typeof(IPrCrawlService))
            .Returns(_ =>
            {
                crawlAttempted.TrySetResult();
                throw new InvalidOperationException("crawl failed");
            });

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var worker = BuildWorker(scopeFactory);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        // Should not throw even though crawl service throws
        var ex = await Record.ExceptionAsync(async () =>
        {
            await worker.StartAsync(CancellationToken.None);
            await crawlAttempted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await worker.StopAsync(CancellationToken.None);
        });

        Assert.Null(ex);
    }

    [Fact]
    public async Task ExecuteAsync_MissingCrawlService_DoesNotThrow()
    {
        var scope = Substitute.For<IServiceScope>();
        var crawlAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scope.ServiceProvider.GetService(typeof(IPrCrawlService)).Returns((object?)null);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope()
            .Returns(_ =>
            {
                crawlAttempted.TrySetResult();
                return scope;
            });

        var worker = BuildWorker(scopeFactory);

        var ex = await Record.ExceptionAsync(async () =>
        {
            await worker.StartAsync(CancellationToken.None);
            await crawlAttempted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await worker.StopAsync(CancellationToken.None);
        });

        Assert.Null(ex);
    }
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Telemetry;
using MeisterDev.ProPR.Api.Workers;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Workers;

/// <summary>
///     What licenses the mention scan, and what it reads to decide there is anything to scan. Both moved when
///     mention answering stopped borrowing the crawl-configuration capability and its repository.
/// </summary>
public sealed class MentionScanWorkerTests
{
    [Fact]
    public async Task ExecuteAsync_WhenMentionAnsweringIsUnavailable_SkipsTheScan()
    {
        var scanService = Substitute.For<IMentionScanService>();
        var (scopeFactory, cycleStarted) = CreateScope(
            scanService,
            CreateLicensingService(PremiumCapabilityKey.MentionAnswering, isAvailable: false));

        await RunOneCycleAsync(scopeFactory, cycleStarted);

        await scanService.DidNotReceiveWithAnyArgs().ScanAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     A client entitled to mention answering but not to crawl configurations used to get no answers,
    ///     because the scan asked about the wrong capability.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithoutCrawlConfigurations_StillScans()
    {
        var scanService = Substitute.For<IMentionScanService>();
        var licensing = CreateLicensingService(PremiumCapabilityKey.MentionAnswering, isAvailable: true);
        licensing.GetCapabilityAsync(PremiumCapabilityKey.CrawlConfigs, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Snapshot(PremiumCapabilityKey.CrawlConfigs, isAvailable: false)));

        var (scopeFactory, cycleStarted) = CreateScope(scanService, licensing);

        await RunOneCycleAsync(scopeFactory, cycleStarted);

        await scanService.Received().ScanAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotReadCrawlConfigurations()
    {
        var scanService = Substitute.For<IMentionScanService>();
        var crawlConfigs = Substitute.For<ICrawlConfigurationRepository>();
        var (scopeFactory, cycleStarted) = CreateScope(
            scanService,
            CreateLicensingService(PremiumCapabilityKey.MentionAnswering, isAvailable: true),
            crawlConfigs);

        await RunOneCycleAsync(scopeFactory, cycleStarted);

        await crawlConfigs.DidNotReceiveWithAnyArgs().GetAllActiveAsync(Arg.Any<CancellationToken>());
    }

    private static async Task RunOneCycleAsync(IServiceScopeFactory scopeFactory, TaskCompletionSource cycleStarted)
    {
        var metricsScope = Substitute.For<IServiceScope>();
        metricsScope.ServiceProvider.GetService(typeof(IJobRepository)).Returns(Substitute.For<IJobRepository>());
        var metricsScopeFactory = Substitute.For<IServiceScopeFactory>();
        metricsScopeFactory.CreateScope().Returns(metricsScope);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MENTION_CRAWL_INTERVAL_SECONDS"] = "10" })
            .Build();

        var worker = new MentionScanWorker(
            scopeFactory,
            new ReviewJobMetrics(metricsScopeFactory),
            configuration,
            NullLogger<MentionScanWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await cycleStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static (IServiceScopeFactory ScopeFactory, TaskCompletionSource CycleStarted) CreateScope(
        IMentionScanService scanService,
        ILicensingCapabilityService licensing,
        ICrawlConfigurationRepository? crawlConfigs = null)
    {
        var cycleStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var mentionConfigs = Substitute.For<IMentionConfigurationRepository>();
        mentionConfigs.GetAllActiveAsync(Arg.Any<CancellationToken>()).Returns([]);

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(ILicensingCapabilityService))
            .Returns(_ =>
            {
                cycleStarted.TrySetResult();
                return licensing;
            });
        serviceProvider.GetService(typeof(IMentionConfigurationRepository)).Returns(mentionConfigs);
        serviceProvider.GetService(typeof(ICrawlConfigurationRepository))
            .Returns(crawlConfigs ?? Substitute.For<ICrawlConfigurationRepository>());
        serviceProvider.GetService(typeof(IMentionScanService)).Returns(scanService);

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(serviceProvider);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        return (scopeFactory, cycleStarted);
    }

    private static ILicensingCapabilityService CreateLicensingService(string capabilityKey, bool isAvailable)
    {
        var licensing = Substitute.For<ILicensingCapabilityService>();
        licensing.GetCapabilityAsync(capabilityKey, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Snapshot(capabilityKey, isAvailable)));
        return licensing;
    }

    private static CapabilitySnapshot Snapshot(string capabilityKey, bool isAvailable)
    {
        return new CapabilitySnapshot(
            capabilityKey,
            capabilityKey,
            true,
            true,
            PremiumCapabilityOverrideState.Default,
            isAvailable,
            isAvailable ? null : $"{capabilityKey} requires a commercial license.");
    }
}

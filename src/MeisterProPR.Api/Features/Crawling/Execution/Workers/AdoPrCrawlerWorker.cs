// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using MeisterProPR.Api.Telemetry;
using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Application.Features.Licensing.Ports;
using MeisterProPR.Application.Features.Licensing.Support;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Api.Workers;

/// <summary>Background worker that periodically crawls ADO for assigned PRs and creates review jobs.</summary>
public sealed partial class AdoPrCrawlerWorker(
    IServiceScopeFactory scopeFactory,
    ReviewJobMetrics metrics,
    IConfiguration configuration,
    ILogger<AdoPrCrawlerWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = configuration.GetValue("PR_CRAWL_INTERVAL_SECONDS", 60);
        if (intervalSeconds < 10)
        {
            intervalSeconds = 10;
        }

        LogWorkerStarted(logger, intervalSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));

        try
        {
            // Run immediately on startup, then on each subsequent tick.
            await this.RunCrawlCycleAsync(stoppingToken);

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await this.RunCrawlCycleAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }

        LogWorkerStopped(logger);
    }

    private async Task RunCrawlCycleAsync(CancellationToken ct)
    {
        using var activity = ReviewJobTelemetry.Source.StartActivity("AdoPrCrawlerWorker.CrawlCycle");
        var sw = Stopwatch.StartNew();
        try
        {
            using var scope = scopeFactory.CreateScope();
            var crawlCapability = await LicensingCapabilityGuard.GetUnavailableCapabilityAsync(
                scope.ServiceProvider.GetService<ILicensingCapabilityService>(),
                PremiumCapabilityKey.CrawlConfigs,
                ct);

            if (crawlCapability is not null)
            {
                return;
            }

            var crawlService = scope.ServiceProvider.GetService<IPrCrawlService>();
            if (crawlService is null)
            {
                return;
            }

            await crawlService.CrawlAsync(ct);
        }
        catch (Exception ex)
        {
            LogCrawlCycleError(logger, ex);
        }
        finally
        {
            sw.Stop();
            metrics.RecordCrawlDuration(ScmProvider.AzureDevOps, sw.Elapsed.TotalSeconds);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "AdoPrCrawlerWorker started (interval: {IntervalSeconds}s)")]
    private static partial void LogWorkerStarted(ILogger logger, int intervalSeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "AdoPrCrawlerWorker stopped")]
    private static partial void LogWorkerStopped(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "AdoPrCrawlerWorker: unhandled exception in crawl cycle — worker continues")]
    private static partial void LogCrawlCycleError(ILogger logger, Exception ex);
}

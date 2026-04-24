// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using MeisterProPR.Api.Telemetry;
using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Application.Features.Licensing.Ports;
using MeisterProPR.Application.Features.Licensing.Support;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Api.Workers;

/// <summary>
///     Background producer worker that periodically scans active pull requests
///     for <c>@bot</c> mentions and enqueues <see cref="MentionReplyJob" /> items
///     into <see cref="System.Threading.Channels.Channel{T}" />.
///     Interval is controlled by <c>MENTION_CRAWL_INTERVAL_SECONDS</c> (default: 60 s, min: 10 s).
/// </summary>
public sealed partial class MentionScanWorker(
    IServiceScopeFactory scopeFactory,
    ReviewJobMetrics metrics,
    IConfiguration configuration,
    ILogger<MentionScanWorker> logger) : BackgroundService
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = configuration.GetValue("MENTION_CRAWL_INTERVAL_SECONDS", 60);
        var interval = TimeSpan.FromSeconds(Math.Max(intervalSeconds, MinInterval.TotalSeconds));

        LogWorkerStarted(logger, interval.TotalSeconds);

        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await this.ScanOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }

        LogWorkerStopped(logger);
    }

    private async Task ScanOnceAsync(CancellationToken stoppingToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var outcome = "completed";
        var providerScope = "none";
        var activeConfigCount = 0;
        var activity = ReviewJobTelemetry.StartActivity("mention_scan.cycle");

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();

            var crawlCapability = await LicensingCapabilityGuard.GetUnavailableCapabilityAsync(
                scope.ServiceProvider.GetService<ILicensingCapabilityService>(),
                PremiumCapabilityKey.CrawlConfigs,
                stoppingToken);

            if (crawlCapability is not null)
            {
                outcome = "skipped_premium_disabled";
                activity?.SetStatus(ActivityStatusCode.Ok, "crawl_configs_disabled");
                return;
            }

            var crawlConfigRepository = scope.ServiceProvider.GetService<ICrawlConfigurationRepository>();
            if (crawlConfigRepository is not null)
            {
                var activeConfigs = await crawlConfigRepository.GetAllActiveAsync(stoppingToken);
                activeConfigCount = activeConfigs.Count;
                providerScope =
                    ReviewJobTelemetry.DescribeProviderScope(activeConfigs.Select(config => config.Provider));
            }

            activity?.SetTag(ReviewJobTelemetry.ScmProviderTagName, providerScope);
            activity?.SetTag("workflow.active_config_count", activeConfigCount);

            using var logScope = logger.BeginScope(
                new Dictionary<string, object?>
                {
                    [ReviewJobTelemetry.ScmProviderTagName] = providerScope,
                    ["active_config_count"] = activeConfigCount,
                });

            var scanService = scope.ServiceProvider.GetService<IMentionScanService>();
            if (scanService is null)
            {
                outcome = "service_unavailable";
                activity?.SetStatus(ActivityStatusCode.Error, "IMentionScanService not registered");
                LogScanServiceUnavailable(logger);
                return;
            }

            await scanService.ScanAsync(stoppingToken);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            outcome = "cancelled";
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            outcome = "failed";
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("error.type", ex.GetType().FullName ?? ex.GetType().Name);
            LogScanCycleError(logger, ex);
        }
        finally
        {
            stopwatch.Stop();
            activity?.SetTag("workflow.outcome", outcome);
            metrics.RecordMentionScanCycle(providerScope, stopwatch.Elapsed.TotalSeconds, outcome, activeConfigCount);
            activity?.Dispose();
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "MentionScanWorker started (interval: {IntervalSeconds:F0}s)")]
    private static partial void LogWorkerStarted(ILogger logger, double intervalSeconds);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "MentionScanWorker stopped")]
    private static partial void LogWorkerStopped(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "MentionScanWorker: IMentionScanService not registered — scan skipped")]
    private static partial void LogScanServiceUnavailable(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "MentionScanWorker: scan cycle failed")]
    private static partial void LogScanCycleError(ILogger logger, Exception ex);
}

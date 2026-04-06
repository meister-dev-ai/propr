// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

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
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var scanService = scope.ServiceProvider.GetService<IMentionScanService>();
            if (scanService is null)
            {
                LogScanServiceUnavailable(logger);
                return;
            }

            await scanService.ScanAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogScanCycleError(logger, ex);
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

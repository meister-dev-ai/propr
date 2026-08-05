// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Threading.Channels;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Observability;

namespace MeisterDev.ProPR.Api.Workers;

/// <summary>
///     Background producer that hands queued thread passes to <see cref="ThreadPassWorker" />.
///     Interval is controlled by <c>THREAD_PASS_SCAN_INTERVAL_SECONDS</c> (default: 30 s, min: 5 s).
/// </summary>
/// <remarks>
///     The passes themselves are created by pull-request synchronization, so this worker's job is only to
///     move durable rows into execution. That also makes it the recovery path: a pass whose process died
///     mid-flight is returned to pending after <see cref="StalledAfter" /> and picked up on a later tick,
///     without its spent attempt being refunded.
/// </remarks>
public sealed partial class ThreadPassScanWorker(
    IServiceScopeFactory scopeFactory,
    ChannelWriter<ThreadPassJob> channelWriter,
    IConfiguration configuration,
    ILogger<ThreadPassScanWorker> logger) : BackgroundService
{
    private const int MaxPassesPerTick = 50;
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StalledAfter = TimeSpan.FromMinutes(15);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = configuration.GetValue("THREAD_PASS_SCAN_INTERVAL_SECONDS", 30);
        var interval = TimeSpan.FromSeconds(Math.Max(intervalSeconds, MinInterval.TotalSeconds));

        LogWorkerStarted(logger, interval.TotalSeconds);

        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await this.DispatchOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        LogWorkerStopped(logger);
    }

    private async Task DispatchOnceAsync(CancellationToken stoppingToken)
    {
        using var background = BackgroundActivityScope.Begin();

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetService<IThreadPassJobRepository>();
            if (repository is null)
            {
                return;
            }

            var sweep = await repository.ReclaimStalledAsync(StalledAfter, stoppingToken);
            if (sweep.ReturnedToPending > 0)
            {
                LogReclaimed(logger, sweep.ReturnedToPending);
            }

            if (sweep.Exhausted > 0)
            {
                LogExhausted(logger, sweep.Exhausted);
            }

            var pending = await repository.GetPendingAsync(MaxPassesPerTick, stoppingToken);
            foreach (var job in pending)
            {
                await channelWriter.WriteAsync(job, stoppingToken);
            }

            // A full batch means passes were left behind. They are durable rows the next tick offers again,
            // but a backlog that never clears is worth saying out loud rather than leaving to be inferred.
            if (pending.Count == MaxPassesPerTick)
            {
                LogBatchFull(logger, MaxPassesPerTick);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogDispatchFailed(logger, ex);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "ThreadPassScanWorker started (interval: {IntervalSeconds:F0}s)")]
    private static partial void LogWorkerStarted(ILogger logger, double intervalSeconds);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "ThreadPassScanWorker stopped")]
    private static partial void LogWorkerStopped(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "ThreadPassScanWorker returned {Count} stalled thread pass(es) to pending")]
    private static partial void LogReclaimed(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "ThreadPassScanWorker failed {Count} thread pass(es) that were abandoned on their last attempt")]
    private static partial void LogExhausted(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "ThreadPassScanWorker dispatched a full batch of {Count} thread pass(es); more are waiting")]
    private static partial void LogBatchFull(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "ThreadPassScanWorker: dispatch cycle failed")]
    private static partial void LogDispatchFailed(ILogger logger, Exception ex);
}

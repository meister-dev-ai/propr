// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.PlatformAdminElevation;

namespace MeisterDev.ProPR.Api.Workers;

/// <summary>
///     Records the expiry of elevations in the audit history of their tenants once a minute. Access ends at the expiry
///     whether or not the worker ran, because every content read compares the expiry with the current time; the worker
///     writes the record of the end. Several instances can run it at the same time, because each expiry is recorded
///     under a concurrency check and therefore once.
/// </summary>
public sealed partial class PlatformAdminElevationExpiryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<PlatformAdminElevationExpiryWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await this.SweepOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    ///     Records the expiries that are due. A failure, including a cancellation that does not come from the shutdown of
    ///     the host, is logged, and the next tick tries again.
    /// </summary>
    internal async Task SweepOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var elevations = scope.ServiceProvider.GetRequiredService<IPlatformAdminElevationService>();
            var recorded = await elevations.RecordExpiriesAsync(stoppingToken);
            if (recorded > 0)
            {
                LogExpiriesRecorded(logger, recorded);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failed sweep is retried on the next tick, and access ended at the expiry regardless.
            LogSweepFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Recorded the expiry of {Count} platform administrator elevations")]
    private static partial void LogExpiriesRecorded(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recording the expiry of platform administrator elevations failed")]
    private static partial void LogSweepFailed(ILogger logger, Exception ex);
}

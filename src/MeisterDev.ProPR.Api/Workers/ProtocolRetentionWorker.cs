// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.TenantData;

namespace MeisterDev.ProPR.Api.Workers;

/// <summary>
///     Deletes the protocol events that are older than the protocol retention of their tenant, once an hour. A tenant
///     without a retention keeps its events.
/// </summary>
public sealed partial class ProtocolRetentionWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    ILogger<ProtocolRetentionWorker> logger) : BackgroundService
{
    internal static TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var purged = await scope.ServiceProvider.GetRequiredService<ITenantDataLifecycle>()
                    .PurgeExpiredProtocolEventsAsync(clock.GetUtcNow(), stoppingToken);
                if (purged > 0)
                {
                    LogPurged(logger, purged);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogFailed(logger, ex);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Deleted {Count} protocol events older than the protocol retention of their tenant")]
    private static partial void LogPurged(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "The protocol retention run failed; the next run tries again")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}

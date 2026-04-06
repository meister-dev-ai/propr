// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Api.Workers;

/// <summary>
///     Background worker that refreshes ProCursor token usage rollups and applies retention.
/// </summary>
public sealed partial class ProCursorTokenUsageRollupWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<ProCursorTokenUsageOptions> options,
    ILogger<ProCursorTokenUsageRollupWorker> logger) : BackgroundService
{
    private readonly ProCursorTokenUsageOptions _options = options.Value;

    /// <summary>
    ///     Whether the worker loop has entered its running state.
    /// </summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    ///     When the current or most recent worker cycle started.
    /// </summary>
    public DateTimeOffset? LastCycleStartedAt { get; private set; }

    /// <summary>
    ///     When the most recent worker cycle completed.
    /// </summary>
    public DateTimeOffset? LastCycleCompletedAt { get; private set; }

    /// <summary>
    ///     When the retention policy last ran successfully.
    /// </summary>
    public DateTimeOffset? LastRetentionRunAtUtc { get; private set; }

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        var intervalSeconds = Math.Max(1, this._options.RollupPollSeconds);
        LogWorkerStarted(logger, intervalSeconds);
        this.IsRunning = true;

        try
        {
            await this.RunCycleAsync(cancellationToken);
            await base.StartAsync(cancellationToken);
        }
        catch
        {
            this.IsRunning = false;
            throw;
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = Math.Max(1, this._options.RollupPollSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await this.RunCycleAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            this.IsRunning = false;
            LogWorkerStopped(logger);
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        this.LastCycleStartedAt = DateTimeOffset.UtcNow;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var aggregationService = scope.ServiceProvider.GetService<IProCursorTokenUsageAggregationService>();
            if (aggregationService is null)
            {
                this.LastCycleCompletedAt = DateTimeOffset.UtcNow;
                return;
            }

            var refreshedBucketCount = await aggregationService.RefreshRecentAsync(ct);

            if (this.ShouldRunRetention(this.LastCycleStartedAt.Value))
            {
                var retentionService = scope.ServiceProvider.GetService<IProCursorTokenUsageRetentionService>();
                if (retentionService is not null)
                {
                    var retentionResult = await retentionService.PurgeExpiredAsync(ct);
                    this.LastRetentionRunAtUtc = retentionResult.PerformedAtUtc;
                    LogRetentionCompleted(logger, retentionResult.DeletedEventCount, retentionResult.DeletedRollupCount);
                }
            }

            this.LastCycleCompletedAt = DateTimeOffset.UtcNow;
            LogCycleCompleted(logger, refreshedBucketCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCycleError(logger, ex);
        }
    }

    private bool ShouldRunRetention(DateTimeOffset cycleStartedAtUtc)
    {
        if (!this.LastRetentionRunAtUtc.HasValue)
        {
            return true;
        }

        return cycleStartedAtUtc - this.LastRetentionRunAtUtc.Value >= TimeSpan.FromDays(1);
    }
}

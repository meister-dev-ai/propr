// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using Microsoft.Extensions.Options;
using MeisterDev.ProPR.CodeInsights;
using MeisterDev.ProPR.CodeInsights.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.CodeInsights.Workers;

/// <summary>
///     Background worker that periodically deletes collected code-insight data whose retention window has
///     elapsed. Retention is evaluated per pull request, anchored on its last collection activity, against
///     an installation-wide window: open pull requests are not exempt.
///     The sweep only ever touches code-insight rows; it never deletes review jobs, file results, protocol
///     traces, thread-memory records, or review-archive data.
///     Window is controlled by <c>CODE_INSIGHTS_RETENTION_DAYS</c> (default: 365 days, min: 1 day) and the
///     interval by <c>CODE_INSIGHTS_PURGE_INTERVAL_SECONDS</c> (default: 3600 s, min: 60 s).
/// </summary>
/// <remarks>
///     Code Insights keeps its own lifecycle deliberately: it reuses the retention concept the review
///     archive established but shares neither its tables, its window, nor its sweep. The default window is
///     much longer than the archive's because the value of the data is the trend over time, and a
///     three-month window would make a year-over-year quality trend impossible.
/// </remarks>
public sealed partial class CodeInsightRetentionPurgeWorker(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<CodeInsightsOptions> options,
    ILogger<CodeInsightRetentionPurgeWorker> logger) : BackgroundService
{
    /// <summary>Window applied when the installation leaves the retention period unset.</summary>
    public const int DefaultRetentionDays = 365;

    private const int MinRetentionDays = 1;

    /// <summary>
    ///     Resolves the retention cutoff at the supplied instant: <paramref name="now" /> minus the
    ///     configured window, floored at <see cref="MinRetentionDays" /> so a misconfigured zero or negative
    ///     value cannot purge data that was just collected.
    /// </summary>
    public static DateTimeOffset ResolveCutoff(int retentionDays, DateTimeOffset now)
    {
        return now - TimeSpan.FromDays(Math.Max(retentionDays, MinRetentionDays));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.CurrentValue.PurgeInterval;

        LogWorkerStarted(logger, interval.TotalSeconds);

        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await this.SweepOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        LogWorkerStopped(logger);
    }

    private async Task SweepOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();

            var store = scope.ServiceProvider.GetService<ICodeInsightRetentionStore>();
            if (store is null)
            {
                LogDependenciesUnavailable(logger);
                return;
            }

            // Read per sweep rather than at startup, so a changed window takes effect without a restart.
            var retentionDays = options.CurrentValue.RetentionDays;
            var cutoff = ResolveCutoff(retentionDays, DateTimeOffset.UtcNow);

            var removed = await store.PurgeExpiredAsync(cutoff, stoppingToken);

            LogSweepCompleted(logger, removed);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed sweep must not tear down the worker loop.
            LogSweepFailed(logger, ex);
        }
    }
}

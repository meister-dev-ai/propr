// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.CodeInsights.Ports;
using MeisterDev.ProPR.Application.Features.CodeInsights;

namespace MeisterDev.ProPR.Api.Features.CodeInsights.Workers;

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
    IConfiguration configuration,
    ILogger<CodeInsightRetentionPurgeWorker> logger) : BackgroundService
{
    /// <summary>Window applied when the installation leaves the retention period unset.</summary>
    public const int DefaultRetentionDays = 365;

    private const int DefaultIntervalSeconds = 3600;
    private const int MinRetentionDays = 1;
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(60);

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
        var intervalSeconds = configuration.GetValue("CODE_INSIGHTS_PURGE_INTERVAL_SECONDS", DefaultIntervalSeconds);
        var interval = TimeSpan.FromSeconds(Math.Max(intervalSeconds, MinInterval.TotalSeconds));

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

            var retentionDays = configuration.GetValue("CODE_INSIGHTS_RETENTION_DAYS", DefaultRetentionDays);
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

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "CodeInsightRetentionPurgeWorker started (interval: {IntervalSeconds:F0}s)")]
    private static partial void LogWorkerStarted(ILogger logger, double intervalSeconds);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "CodeInsightRetentionPurgeWorker stopped")]
    private static partial void LogWorkerStopped(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "CodeInsightRetentionPurgeWorker sweep completed (pull requests removed: {RemovedCount})")]
    private static partial void LogSweepCompleted(ILogger logger, int removedCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "CodeInsightRetentionPurgeWorker: code-insight store not registered: sweep skipped")]
    private static partial void LogDependenciesUnavailable(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "CodeInsightRetentionPurgeWorker: sweep cycle failed")]
    private static partial void LogSweepFailed(ILogger logger, Exception ex);
}

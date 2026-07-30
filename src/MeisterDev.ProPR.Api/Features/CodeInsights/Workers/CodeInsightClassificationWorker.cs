// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.CodeInsights.Ports;

namespace MeisterDev.ProPR.Api.Features.CodeInsights.Workers;

/// <summary>
///     Background worker that classifies collected findings by type, post-hoc and off the review path. It
///     drains a bounded batch per cycle; a burst of findings therefore takes several cycles rather than
///     saturating the client's model quota in one go.
///     Interval is controlled by <c>CODE_INSIGHTS_CLASSIFICATION_INTERVAL_SECONDS</c>
///     (default: 60 s, min: 10 s).
/// </summary>
public sealed partial class CodeInsightClassificationWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<CodeInsightClassificationWorker> logger) : BackgroundService
{
    private const int DefaultIntervalSeconds = 60;
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = configuration.GetValue(
            "CODE_INSIGHTS_CLASSIFICATION_INTERVAL_SECONDS",
            DefaultIntervalSeconds);
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

            var sweeper = scope.ServiceProvider.GetService<ICodeInsightClassificationSweeper>();
            if (sweeper is null)
            {
                LogDependenciesUnavailable(logger);
                return;
            }

            var result = await sweeper.SweepOnceAsync(stoppingToken);

            // A backlog that keeps growing is the failure mode worth seeing, so it is reported every cycle that
            // did any work rather than only when something went wrong.
            if (result.Considered > 0)
            {
                LogSweepCompleted(
                    logger,
                    result.Considered,
                    result.Classified,
                    result.Failed,
                    result.SkippedByGate,
                    result.BacklogRemaining);
            }
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
        Message = "CodeInsightClassificationWorker started (interval: {IntervalSeconds:F0}s)")]
    private static partial void LogWorkerStarted(ILogger logger, double intervalSeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "CodeInsightClassificationWorker stopped")]
    private static partial void LogWorkerStopped(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "CodeInsightClassificationWorker sweep: considered {Considered}, classified {Classified}, "
                  + "failed {Failed}, skipped by gate {SkippedByGate}, backlog remaining {BacklogRemaining}")]
    private static partial void LogSweepCompleted(
        ILogger logger,
        int considered,
        int classified,
        int failed,
        int skippedByGate,
        int backlogRemaining);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "CodeInsightClassificationWorker: the classification sweeper is not registered: sweep skipped")]
    private static partial void LogDependenciesUnavailable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "CodeInsightClassificationWorker: sweep cycle failed")]
    private static partial void LogSweepFailed(ILogger logger, Exception ex);
}

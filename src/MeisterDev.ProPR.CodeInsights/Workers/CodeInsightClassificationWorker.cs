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
///     Background worker that classifies collected findings by type, post-hoc and off the review path. It
///     drains a bounded batch per cycle; a burst of findings therefore takes several cycles rather than
///     saturating the client's model quota in one go.
///     Interval is controlled by <c>CODE_INSIGHTS_CLASSIFICATION_INTERVAL_SECONDS</c>
///     (default: 60 s, min: 10 s).
/// </summary>
public sealed partial class CodeInsightClassificationWorker(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<CodeInsightsOptions> options,
    ILogger<CodeInsightClassificationWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.CurrentValue.ClassificationInterval;

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
}

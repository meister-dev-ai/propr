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
///     Background worker that catches up on the two things the live paths can miss: roll-up cells for findings
///     collected before the projection existed, and measurements for pull requests whose closure the
///     synchronization path never observed.
/// </summary>
public sealed partial class CodeInsightCatchUpWorker(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<CodeInsightsOptions> options,
    ILogger<CodeInsightCatchUpWorker> logger) : BackgroundService
{
    /// <summary>Jobs the projection backfill projects per cycle when left unset.</summary>
    public const int DefaultBackfillMaxJobs = 50;

    /// <summary>Pull requests the seal sweep examines per cycle when left unset.</summary>
    public const int DefaultSealSweepMaxPullRequests = 25;

    /// <summary>Days without collection activity before a pull request is asked about, when left unset.</summary>
    public const int DefaultSealSweepIdleDays = 7;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.CurrentValue.CatchUpInterval;

        LogWorkerStarted(logger, interval.TotalSeconds);

        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await this.CatchUpOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        LogWorkerStopped(logger);
    }

    private async Task CatchUpOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();

            var projector = scope.ServiceProvider.GetService<ICodeInsightRollupProjector>();
            var sweeper = scope.ServiceProvider.GetService<ICodeInsightSealSweeper>();
            if (projector is null || sweeper is null)
            {
                LogDependenciesUnavailable(logger);
                return;
            }

            // One read per sweep, so a bound raised while the host runs applies to the next sweep.
            var current = options.CurrentValue;

            var projected = await projector.BackfillAsync(current.EffectiveBackfillMaxJobs, stoppingToken);

            var sealedCount = await sweeper.SweepAsync(
                Math.Max(current.SealSweepMaxPullRequests, 1),
                TimeSpan.FromDays(Math.Max(current.SealSweepIdleDays, 1)),
                stoppingToken);

            LogCycleCompleted(logger, projected, sealedCount);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed cycle must not tear down the worker loop.
            LogCycleFailed(logger, ex);
        }
    }
}

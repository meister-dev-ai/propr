// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Api.Workers;

/// <summary>
///     Background worker that back-fills search keywords onto thread memories stored before keyword extraction
///     existed. Off unless <c>AI_MEMORY_KEYWORD_BACKFILL_MAX</c> is above zero, with the interval set by
///     <c>AI_MEMORY_KEYWORD_SWEEP_INTERVAL_SECONDS</c> (default 21600 s, min 300 s).
/// </summary>
/// <remarks>
///     <para>
///         Its own worker rather than a passenger on another sweep. It used to ride on the Code Insights
///         catch-up worker, which is where keyword extraction was first built, but keywords describe a thread
///         memory rather than a review finding, so a worker that stops when insights are switched off would stop
///         the wrong thing.
///     </para>
///     <para>
///         The budget is read once per sweep, so raising it while the host runs applies on the next pass. Zero,
///         the default, makes each pass a no-op costing one options read.
///     </para>
/// </remarks>
public sealed partial class ThreadMemoryKeywordWorker(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<ThreadMemoryKeywordOptions> options,
    ILogger<ThreadMemoryKeywordWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogWorkerStarted(logger, options.CurrentValue.SweepInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await this.SweepOnceAsync(stoppingToken);

            try
            {
                await Task.Delay(options.CurrentValue.SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        LogWorkerStopped(logger);
    }

    private async Task SweepOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            var budget = options.CurrentValue.EffectiveBackfillMax;
            if (budget == 0)
            {
                return;
            }

            await using var scope = scopeFactory.CreateAsyncScope();

            var sweeper = scope.ServiceProvider.GetService<IThreadMemoryKeywordSweeper>();
            if (sweeper is null)
            {
                LogSweeperUnavailable(logger);
                return;
            }

            var enriched = await sweeper.SweepAsync(budget, stoppingToken);
            LogSweepCompleted(logger, enriched);
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

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.CodeInsights.Ports;

namespace MeisterDev.ProPR.Api.Features.CodeInsights.Workers;

/// <summary>
///     Background worker that catches up on the two things the live paths can miss: roll-up cells for findings
///     collected before the projection existed, and measurements for pull requests whose closure the
///     synchronization path never observed.
/// </summary>
/// <remarks>
///     <para>
///         Both sweeps are bounded per cycle and resumable by construction: their candidates are derived from
///         what is still missing, so a cycle that stops halfway just leaves fewer candidates for the next one.
///         Both are per-client gated, and neither writes anything the collection path reads.
///     </para>
///     <para>
///         Batch sizes and cadence are configuration:
///         <c>CODE_INSIGHTS_CATCHUP_INTERVAL_SECONDS</c> (default 21600 s, min 600 s),
///         <c>CODE_INSIGHTS_BACKFILL_MAX_JOBS</c> (default 50 jobs per cycle),
///         <c>CODE_INSIGHTS_SEAL_SWEEP_MAX_PULL_REQUESTS</c> (default 25 per cycle, each costs one provider
///         call), and <c>CODE_INSIGHTS_SEAL_SWEEP_IDLE_DAYS</c> (default 7 days of no collection activity before
///         a pull request is considered quiet enough to ask about).
///     </para>
///     <para>
///         The resolution-memory keyword backfill is <strong>off by default</strong> and enabled by setting
///         <c>CODE_INSIGHTS_MEMORY_KEYWORD_BACKFILL_MAX</c> above zero. Every row it touches costs a model call,
///         and a sweep that quietly spends tokens on years of old memories is not a backfill anybody asked for.
///     </para>
/// </remarks>
public sealed partial class CodeInsightCatchUpWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<CodeInsightCatchUpWorker> logger) : BackgroundService
{
    /// <summary>Jobs the projection backfill projects per cycle when left unset.</summary>
    public const int DefaultBackfillMaxJobs = 50;

    /// <summary>Pull requests the seal sweep examines per cycle when left unset.</summary>
    public const int DefaultSealSweepMaxPullRequests = 25;

    /// <summary>Days without collection activity before a pull request is asked about, when left unset.</summary>
    public const int DefaultSealSweepIdleDays = 7;

    /// <summary>
    ///     Memories the keyword backfill enriches per cycle when left unset. Zero: it costs model calls, so an
    ///     installation opts in rather than discovering the spend afterwards.
    /// </summary>
    public const int DefaultMemoryKeywordBackfillMax = 0;

    private const int DefaultIntervalSeconds = 21600;
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(600);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = configuration.GetValue("CODE_INSIGHTS_CATCHUP_INTERVAL_SECONDS", DefaultIntervalSeconds);
        var interval = TimeSpan.FromSeconds(Math.Max(intervalSeconds, MinInterval.TotalSeconds));

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
            var keywords = scope.ServiceProvider.GetService<ICodeInsightMemoryKeywordSweeper>();
            if (projector is null || sweeper is null)
            {
                LogDependenciesUnavailable(logger);
                return;
            }

            var projected = await projector.BackfillAsync(
                Math.Max(configuration.GetValue("CODE_INSIGHTS_BACKFILL_MAX_JOBS", DefaultBackfillMaxJobs), 1),
                stoppingToken);

            var idleDays = Math.Max(
                configuration.GetValue("CODE_INSIGHTS_SEAL_SWEEP_IDLE_DAYS", DefaultSealSweepIdleDays),
                1);

            var sealedCount = await sweeper.SweepAsync(
                Math.Max(
                    configuration.GetValue(
                        "CODE_INSIGHTS_SEAL_SWEEP_MAX_PULL_REQUESTS",
                        DefaultSealSweepMaxPullRequests),
                    1),
                TimeSpan.FromDays(idleDays),
                stoppingToken);

            var keywordBudget = Math.Max(
                configuration.GetValue(
                    "CODE_INSIGHTS_MEMORY_KEYWORD_BACKFILL_MAX",
                    DefaultMemoryKeywordBackfillMax),
                0);

            var enriched = keywordBudget > 0 && keywords is not null
                ? await keywords.SweepAsync(keywordBudget, stoppingToken)
                : 0;

            LogCycleCompleted(logger, projected, sealedCount, enriched);
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

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "CodeInsightCatchUpWorker started (interval: {IntervalSeconds:F0}s)")]
    private static partial void LogWorkerStarted(ILogger logger, double intervalSeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "CodeInsightCatchUpWorker stopped")]
    private static partial void LogWorkerStopped(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "CodeInsightCatchUpWorker cycle completed (jobs projected: {ProjectedCount}, "
                  + "pull requests sealed: {SealedCount}, memories enriched: {EnrichedCount})")]
    private static partial void LogCycleCompleted(
        ILogger logger,
        int projectedCount,
        int sealedCount,
        int enrichedCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "CodeInsightCatchUpWorker: the code-insight module is not registered: catch-up skipped")]
    private static partial void LogDependenciesUnavailable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "CodeInsightCatchUpWorker: catch-up cycle failed")]
    private static partial void LogCycleFailed(ILogger logger, Exception ex);
}

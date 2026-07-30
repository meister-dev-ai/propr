// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.CodeInsights.Events;
using MeisterDev.ProPR.Application.Interfaces;

namespace MeisterDev.ProPR.Api.Features.CodeInsights.Workers;

/// <summary>
///     Background worker that periodically evaluates the code-insight quality conditions per client and records
///     any transitions. The rows it writes are the queryable contract a notification or alerting capability will
///     consume; nothing here delivers anything, and no consumer has to exist.
/// </summary>
/// <remarks>
///     <para>
///         Thresholds are configuration with provisional defaults, because they are uncalibrated:
///         <c>CODE_INSIGHTS_CONDITION_WINDOW_DAYS</c> (default 28),
///         <c>CODE_INSIGHTS_F1_DECLINE_THRESHOLD</c> (0.10),
///         <c>CODE_INSIGHTS_FALSE_POSITIVE_SHARE_THRESHOLD</c> (0.30),
///         <c>CODE_INSIGHTS_CONCENTRATION_THRESHOLD</c> (25 findings in one file),
///         and the interval via <c>CODE_INSIGHTS_CONDITION_INTERVAL_SECONDS</c> (3600 s, min 300 s).
///         The correctness condition additionally honours
///         <c>CODE_INSIGHTS_MIN_SEALED_PULL_REQUESTS</c>, so it cannot fire on a sample too thin to mean anything.
///     </para>
///     <para>
///         An evaluation reads only derived records and writes only to its own table, so a failure costs one cycle
///         of alerting latency and never disturbs collection, sealing, or projection.
///     </para>
/// </remarks>
public sealed partial class CodeInsightConditionWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<CodeInsightConditionWorker> logger) : BackgroundService
{
    /// <summary>Window a condition looks back over when the installation leaves it unset.</summary>
    public const int DefaultWindowDays = 28;

    /// <summary>Correctness fall across the window that counts as a decline when left unset.</summary>
    public const double DefaultCorrectnessDeclineThreshold = 0.10;

    /// <summary>Share of resolved findings judged wrong that counts as too noisy when left unset.</summary>
    public const double DefaultFalsePositiveShareThreshold = 0.30;

    /// <summary>Findings in one file within the window that counts as a hotspot when left unset.</summary>
    public const int DefaultConcentrationThreshold = 25;

    private const int DefaultIntervalSeconds = 3600;
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(300);

    /// <summary>Reads the configured thresholds, flooring each so a misconfiguration cannot fire on everything.</summary>
    public static CodeInsightConditionThresholds ResolveThresholds(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new CodeInsightConditionThresholds(
            Math.Max(configuration.GetValue("CODE_INSIGHTS_CONDITION_WINDOW_DAYS", DefaultWindowDays), 1),
            // A zero or negative decline threshold would make every wobble a transition, which is the opposite of
            // an alert; the same reasoning floors the other two.
            Math.Max(
                configuration.GetValue("CODE_INSIGHTS_F1_DECLINE_THRESHOLD", DefaultCorrectnessDeclineThreshold),
                0.01),
            Math.Max(
                configuration.GetValue(
                    "CODE_INSIGHTS_FALSE_POSITIVE_SHARE_THRESHOLD",
                    DefaultFalsePositiveShareThreshold),
                0.01),
            Math.Max(
                configuration.GetValue("CODE_INSIGHTS_CONCENTRATION_THRESHOLD", DefaultConcentrationThreshold),
                1),
            Math.Max(configuration.GetValue("CODE_INSIGHTS_MIN_SEALED_PULL_REQUESTS", 10), 1));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = configuration.GetValue("CODE_INSIGHTS_CONDITION_INTERVAL_SECONDS", DefaultIntervalSeconds);
        var interval = TimeSpan.FromSeconds(Math.Max(intervalSeconds, MinInterval.TotalSeconds));

        LogWorkerStarted(logger, interval.TotalSeconds);

        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await this.EvaluateOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        LogWorkerStopped(logger);
    }

    private async Task EvaluateOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();

            var evaluator = scope.ServiceProvider.GetService<ICodeInsightConditionEvaluator>();
            var clients = scope.ServiceProvider.GetService<IClientAdminService>();
            if (evaluator is null || clients is null)
            {
                LogDependenciesUnavailable(logger);
                return;
            }

            var thresholds = ResolveThresholds(configuration);
            var asOf = DateOnly.FromDateTime(DateTime.UtcNow);
            var recorded = 0;

            // Per client, because the collection gate is per client and a condition means nothing across tenants.
            foreach (var client in await clients.GetAllAsync(stoppingToken))
            {
                recorded += await evaluator.EvaluateAsync(client.Id, asOf, thresholds, stoppingToken);
            }

            LogCycleCompleted(logger, recorded);
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
        Message = "CodeInsightConditionWorker started (interval: {IntervalSeconds:F0}s)")]
    private static partial void LogWorkerStarted(ILogger logger, double intervalSeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "CodeInsightConditionWorker stopped")]
    private static partial void LogWorkerStopped(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "CodeInsightConditionWorker cycle completed (transitions recorded: {RecordedCount})")]
    private static partial void LogCycleCompleted(ILogger logger, int recordedCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "CodeInsightConditionWorker: the code-insight module is not registered: evaluation skipped")]
    private static partial void LogDependenciesUnavailable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "CodeInsightConditionWorker: evaluation cycle failed")]
    private static partial void LogCycleFailed(ILogger logger, Exception ex);
}

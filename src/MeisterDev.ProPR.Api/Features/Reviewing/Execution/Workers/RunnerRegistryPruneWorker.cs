// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

namespace MeisterDev.ProPR.Api.Workers;

/// <summary>
///     Removes runners that have stopped calling in.
///     <para>
///         A runner's credential is held in memory, so a host that restarts enrolls again as a new runner
///         and the row it used before stays in the registry. Under a deployment that scales itself, every
///         replica that starts adds a row that is never used again, and the registry grows without limit.
///     </para>
///     <para>
///         Deletion uses the same service as an operator's delete, so a runner still holding a lease is
///         refused here as it would be there. Removing the row of a runner that is still executing a
///         review would interrupt that review.
///     </para>
///     <para>
///         Controlled by <c>RUNNER_PRUNE_UNSEEN_DAYS</c> (default 30, set to 0 to keep every row forever)
///         and <c>RUNNER_PRUNE_INTERVAL_SECONDS</c> (default 3600, minimum 60).
///     </para>
/// </summary>
public sealed partial class RunnerRegistryPruneWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<RunnerRegistryPruneWorker> logger) : BackgroundService
{
    /// <summary>How long a runner may be silent before it is considered gone.</summary>
    public const int DefaultUnseenDays = 30;

    private const int DefaultIntervalSeconds = 3600;
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     Upper bound on the rows one sweep removes. An installation that has accumulated thousands
    ///     removes them over several sweeps instead of in one long statement. The next sweep runs an hour
    ///     later by default.
    /// </summary>
    private const int MaxPerSweep = 200;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var unseenDays = configuration.GetValue("RUNNER_PRUNE_UNSEEN_DAYS", DefaultUnseenDays);
        if (unseenDays <= 0)
        {
            LogPruningDisabled(logger);
            return;
        }

        var intervalSeconds = configuration.GetValue("RUNNER_PRUNE_INTERVAL_SECONDS", DefaultIntervalSeconds);
        var interval = TimeSpan.FromSeconds(Math.Max(intervalSeconds, MinInterval.TotalSeconds));

        LogWorkerStarted(logger, unseenDays, interval.TotalSeconds);

        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await this.SweepOnceAsync(TimeSpan.FromDays(unseenDays), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    ///     Removes one sweep's worth of silent runners and reports what it did.
    /// </summary>
    /// <param name="unseenFor">How long a runner must have been silent to be reaped.</param>
    /// <param name="ct">The cancellation token.</param>
    internal async Task<int> SweepOnceAsync(TimeSpan unseenFor, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var registry = scope.ServiceProvider.GetService<IRunnerRegistry>();
            var runners = scope.ServiceProvider.GetService<IRunnerRegistrationService>();
            if (registry is null || runners is null)
            {
                return 0;
            }

            var cutoff = timeProvider.GetUtcNow() - unseenFor;
            var candidates = await registry.ListUnseenSinceAsync(cutoff, MaxPerSweep, ct);
            if (candidates.Count == 0)
            {
                return 0;
            }

            var removed = 0;
            var held = 0;
            foreach (var runnerId in candidates)
            {
                switch (await runners.DeleteAsync(runnerId, ct))
                {
                    case RunnerDeletionOutcome.Deleted:
                        removed++;
                        break;

                    // A silent runner that still holds a lease is handled by the reclaim path first. It
                    // is left in place and removed by a later sweep.
                    case RunnerDeletionOutcome.HoldingLease:
                        held++;
                        break;

                    default:
                        break;
                }
            }

            if (removed > 0 || held > 0)
            {
                LogSweepCompleted(logger, removed, held, cutoff);
            }

            return removed;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A failed sweep must not stop the host. The next sweep retries.
            LogSweepFailed(logger, exception);
            return 0;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Runner registry pruning is disabled; silent runners will be kept until an operator deletes them.")]
    private static partial void LogPruningDisabled(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Runner registry prune worker started (unseen for {UnseenDays}d, every {IntervalSeconds}s).")]
    private static partial void LogWorkerStarted(ILogger logger, int unseenDays, double intervalSeconds);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Pruned {Removed} runner(s) unseen since {Cutoff}; {HoldingLease} still held a lease and were left.")]
    private static partial void LogSweepCompleted(ILogger logger, int removed, int holdingLease, DateTimeOffset cutoff);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Runner registry prune sweep failed; the next sweep retries.")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}

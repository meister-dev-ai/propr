// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.UsageStatistics.Models;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Ports;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Services;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Support;
using MeisterDev.ProPR.Observability;

namespace MeisterDev.ProPR.Api.Workers;

/// <summary>
///     Sends at most one anonymous usage snapshot a day, and none while the installation is switched off or
///     has not yet shown the consent notice to an administrator.
///     <para>
///         Each cycle reads one row and, in the disabled and pre-consent states, returns without building a
///         snapshot or resolving the transport. The decision is taken before any HTTP client is constructed, so
///         a cycle in those states performs no outbound request.
///     </para>
/// </summary>
public sealed partial class UsageStatisticsSendWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<UsageStatisticsSendWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogWorkerStarted(logger);

        try
        {
            var delay = await this.ResolveInitialDelayAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(delay, timeProvider, stoppingToken);
                delay = this.ResolveNextDelay(await this.RunCycleOnceAsync(stoppingToken));
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        LogWorkerStopped(logger);
    }

    /// <summary>Runs one cycle, which sends only if this installation is due to.</summary>
    internal async Task<UsageStatisticsCycleResult?> RunCycleOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetService<UsageStatisticsSender>();
            if (sender is null)
            {
                return new UsageStatisticsCycleResult(UsageStatisticsSendDecision.Disabled, null);
            }

            // Background work is excluded from outbound tracing by default, so a daily ping does not appear as
            // a span in every installation that exports traces.
            using var background = BackgroundActivityScope.Begin();
            return await sender.SendIfDueAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A dropped snapshot is not retried; the next cycle builds a fresh one.
            LogCycleFailed(logger, exception);
            return null;
        }
    }

    /// <summary>
    ///     Determines how long to wait after a cycle, from the decision that cycle produced.
    ///     <para>
    ///         Scheduling from the stored timestamp instead would read a value that never changes in the two
    ///         states where nothing is sent. The wait would collapse onto its one-minute floor and poll the
    ///         database continuously on an installation that switched the feature off.
    ///     </para>
    /// </summary>
    internal TimeSpan ResolveNextDelay(UsageStatisticsCycleResult? result)
    {
        var now = timeProvider.GetUtcNow();

        return result?.Decision switch
        {
            // A send consumes the day whether or not the outcome could be stored.
            UsageStatisticsSendDecision.Sent =>
                UsageStatisticsSendSchedule.NextDelay(now, now, Random.Shared.NextDouble()),

            // These states only change when an operator acts, so recheck at the idle interval.
            UsageStatisticsSendDecision.Disabled or UsageStatisticsSendDecision.AwaitingConsent =>
                UsageStatisticsSendSchedule.IdleRecheckInterval,

            UsageStatisticsSendDecision.NotDue =>
                UsageStatisticsSendSchedule.NextDelay(result.LastAttemptAt, now, Random.Shared.NextDouble()),

            // A cycle that threw. Backing off to the idle interval keeps a persistent fault from becoming a
            // tight loop.
            _ => UsageStatisticsSendSchedule.IdleRecheckInterval,
        };
    }

    /// <summary>
    ///     Determines how long to wait before the first cycle, from the last attempt this installation recorded.
    ///     <para>
    ///         Reading the stored timestamp rather than counting from process start prevents a host that
    ///         restarts every few hours from sending several times a day.
    ///     </para>
    /// </summary>
    private async Task<TimeSpan> ResolveInitialDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetService<IUsageStatisticsStateStore>();
            if (store is null)
            {
                return UsageStatisticsSendSchedule.Cadence;
            }

            var state = await store.GetAsync(cancellationToken);
            return UsageStatisticsSendSchedule.NextDelay(
                state.LastAttemptAt,
                timeProvider.GetUtcNow(),
                Random.Shared.NextDouble());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogScheduleUnavailable(logger, exception);
            return UsageStatisticsSendSchedule.Cadence;
        }
    }
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.ReviewArchive;
using MeisterProPR.Application.Interfaces;

namespace MeisterProPR.Api.Workers;

/// <summary>
///     Background worker that periodically deletes retained raw pull-request data (archived threads,
///     comments, and diffs) whose retention window has elapsed. Retention is evaluated per pull
///     request, anchored on its last activity, against the owning connection's window — open pull
///     requests are not exempt. When a connection has both retention toggles off, all of its archived
///     data is purged. The sweep only ever touches review-archive rows; it never deletes review jobs,
///     file results, findings, protocol traces, or thread-memory records.
///     Interval is controlled by <c>REVIEW_ARCHIVE_PURGE_INTERVAL_SECONDS</c> (default: 3600 s, min: 60 s).
/// </summary>
public sealed partial class RetentionPurgeWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<RetentionPurgeWorker> logger) : BackgroundService
{
    /// <summary>The action a single connection's retention settings imply for one sweep.</summary>
    public enum RetentionAction
    {
        /// <summary>Both toggles are off: delete every archived row for the connection.</summary>
        PurgeAllForConnection,

        /// <summary>Retention is enabled: delete archived pull requests older than the cutoff.</summary>
        PurgeExpired,
    }

    /// <summary>Window applied when a connection leaves its retention period unset.</summary>
    public const int DefaultRetentionDays = 30;

    private const int DefaultIntervalSeconds = 3600;
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     Resolves the retention decision for a single connection at the supplied instant. When both
    ///     retention toggles are off the connection's archived data is purged wholesale; otherwise the
    ///     cutoff is <paramref name="now" /> minus the connection's window (defaulting to
    ///     <see cref="DefaultRetentionDays" /> days when unset).
    /// </summary>
    public static RetentionDecision DecideForConnection(ClientScmConnectionRetentionDto connection, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (!connection.StoreThreads && !connection.StoreDiffs)
        {
            return new RetentionDecision(connection.Id, RetentionAction.PurgeAllForConnection, default);
        }

        var window = TimeSpan.FromDays(connection.RetentionDays ?? DefaultRetentionDays);
        return new RetentionDecision(connection.Id, RetentionAction.PurgeExpired, now - window);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = configuration.GetValue("REVIEW_ARCHIVE_PURGE_INTERVAL_SECONDS", DefaultIntervalSeconds);
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

            var connectionRepository = scope.ServiceProvider.GetService<IClientScmConnectionRepository>();
            var archiveStore = scope.ServiceProvider.GetService<IReviewArchiveStore>();
            if (connectionRepository is null || archiveStore is null)
            {
                LogDependenciesUnavailable(logger);
                return;
            }

            var connections = await connectionRepository.GetAllForRetentionSweepAsync(stoppingToken);
            var now = DateTimeOffset.UtcNow;
            var removedTotal = 0;

            foreach (var connection in connections)
            {
                stoppingToken.ThrowIfCancellationRequested();

                try
                {
                    var decision = DecideForConnection(connection, now);

                    // The archive purge deletes each pull request's retained data and its posted-comment
                    // provenance together, one transaction per pull request (see ReviewArchiveStore), so no
                    // separate provenance pass is needed here.
                    removedTotal += decision.Action == RetentionAction.PurgeAllForConnection
                        ? await archiveStore.PurgeForConnectionAsync(decision.ConnectionId, stoppingToken)
                        : await archiveStore.PurgeExpiredForConnectionAsync(decision.ConnectionId, decision.Cutoff, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One connection failing must not abort the rest of the sweep.
                    LogConnectionPurgeFailed(logger, connection.Id, ex);
                }
            }

            LogSweepCompleted(logger, connections.Count, removedTotal);
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
        Message = "RetentionPurgeWorker started (interval: {IntervalSeconds:F0}s)")]
    private static partial void LogWorkerStarted(ILogger logger, double intervalSeconds);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "RetentionPurgeWorker stopped")]
    private static partial void LogWorkerStopped(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "RetentionPurgeWorker sweep completed (connections: {ConnectionCount}, pull requests removed: {RemovedCount})")]
    private static partial void LogSweepCompleted(ILogger logger, int connectionCount, int removedCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "RetentionPurgeWorker: review-archive dependencies not registered — sweep skipped")]
    private static partial void LogDependenciesUnavailable(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "RetentionPurgeWorker: purge failed for connection {ConnectionId}")]
    private static partial void LogConnectionPurgeFailed(ILogger logger, Guid connectionId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "RetentionPurgeWorker: sweep cycle failed")]
    private static partial void LogSweepFailed(ILogger logger, Exception ex);

    /// <summary>The resolved retention decision for one connection in one sweep.</summary>
    /// <param name="ConnectionId">The connection the decision applies to.</param>
    /// <param name="Action">Whether to purge everything or only expired pull requests.</param>
    /// <param name="Cutoff">
    ///     For <see cref="RetentionAction.PurgeExpired" />, pull requests with last activity strictly
    ///     before this instant are removed. Unused for <see cref="RetentionAction.PurgeAllForConnection" />.
    /// </param>
    public readonly record struct RetentionDecision(Guid ConnectionId, RetentionAction Action, DateTimeOffset Cutoff);
}

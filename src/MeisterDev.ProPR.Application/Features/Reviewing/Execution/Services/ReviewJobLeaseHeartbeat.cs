// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>
///     Keeps one job's lease alive for as long as it is being executed, on its own schedule. Renewal must not
///     depend on the pipeline reaching a checkpoint: a single AI or tool call can outlast a whole lease
///     duration, and a review that is working perfectly well would otherwise lose its job to a reclaim.
///     <para>
///         When the lease can no longer be renewed, either because someone else now holds it or because the
///         database has been unreachable for too many attempts in a row, <see cref="LeaseLost" /> is
///         cancelled. Continuing to work past that point means two parties reviewing the same job, so the
///         caller is expected to stop.
///     </para>
/// </summary>
public sealed partial class ReviewJobLeaseHeartbeat : IAsyncDisposable
{
    private readonly CancellationTokenSource _leaseLost = new();
    private readonly ReviewJobLease _lease;
    private readonly ILogger _logger;
    private readonly ReviewLeaseOptions _options;
    private readonly CancellationTokenSource _stopping;
    private readonly IReviewJobLeaseStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly Task _loop;
    private int _disposed;

    private ReviewJobLeaseHeartbeat(
        ReviewJobLease lease,
        IReviewJobLeaseStore store,
        ReviewLeaseOptions options,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken stoppingToken)
    {
        this._lease = lease;
        this._store = store;
        this._options = options;
        this._timeProvider = timeProvider;
        this._logger = logger;
        this._stopping = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        this._loop = Task.Run(() => this.RunAsync(this._stopping.Token), CancellationToken.None);
    }

    /// <summary>
    ///     Cancelled once the lease is no longer held. Link this into the token the review runs under so
    ///     losing the lease reaches in-flight calls rather than being noticed at the next checkpoint.
    /// </summary>
    public CancellationToken LeaseLost => this._leaseLost.Token;

    /// <summary>True once renewal has stopped because the lease was lost.</summary>
    public bool IsLeaseLost => this._leaseLost.IsCancellationRequested;

    /// <summary>
    ///     Why work on this job stopped, once it has. The heartbeat is the only channel that reaches an
    ///     execution wherever it runs, so a stop, a supersede, and a budget cut all arrive here, and the
    ///     holder needs the reason to finalise the job as the right thing rather than as a generic failure.
    /// </summary>
    public ReviewJobStopReason StopReason { get; private set; } = ReviewJobStopReason.None;

    /// <summary>Starts renewing the supplied lease in the background.</summary>
    /// <param name="lease">The lease to keep alive.</param>
    /// <param name="store">The claiming and liveness boundary.</param>
    /// <param name="options">Lease duration, interval, jitter, and failure tolerance.</param>
    /// <param name="timeProvider">Clock used for the renewal schedule.</param>
    /// <param name="logger">Logger for renewal outcomes.</param>
    /// <param name="stoppingToken">Cancelled to stop renewing, for example on host shutdown.</param>
    public static ReviewJobLeaseHeartbeat Start(
        ReviewJobLease lease,
        IReviewJobLeaseStore store,
        ReviewLeaseOptions options,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken stoppingToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        return new ReviewJobLeaseHeartbeat(lease, store, options, timeProvider, logger, stoppingToken);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Idempotent. Callers stop renewing before they write a job's terminal state, and the enclosing
    ///     <c>await using</c> then disposes a second time.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this._disposed, 1) == 1)
        {
            return;
        }

        await this._stopping.CancelAsync();

        try
        {
            await this._loop;
        }
        catch (OperationCanceledException)
        {
            // Expected: stopping the heartbeat cancels its own delay.
        }

        this._stopping.Dispose();
        this._leaseLost.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var consecutiveFailures = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(this.NextDelay(consecutiveFailures), this._timeProvider, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                var renewal = await this._store.TryRenewAsync(this._lease, this._options.LeaseDuration, ct);
                if (renewal.Directive == ReviewJobDirective.Stop)
                {
                    // Definitive rather than transient, whichever reason it carries: either somebody decided
                    // this job should stop, or this party no longer owns it. Retrying changes neither.
                    this.StopReason = renewal.StopReason;
                    LogStopDirective(this._logger, this._lease.JobId, this._lease.Generation, renewal.StopReason);
                    await this._leaseLost.CancelAsync();
                    return;
                }

                if (!renewal.Accepted)
                {
                    this.StopReason = ReviewJobStopReason.LeaseNoLongerHeld;
                    LogStopDirective(
                        this._logger,
                        this._lease.JobId,
                        this._lease.Generation,
                        ReviewJobStopReason.LeaseNoLongerHeld);
                    await this._leaseLost.CancelAsync();
                    return;
                }

                consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                LogRenewalFailed(this._logger, this._lease.JobId, consecutiveFailures, ex);

                if (consecutiveFailures >= this._options.MaxConsecutiveHeartbeatFailures)
                {
                    // The lease will expire on its own and someone else will reclaim the job. Working on
                    // without a renewable lease is what produces two executions of the same review.
                    this.StopReason = ReviewJobStopReason.LeaseNoLongerHeld;
                    LogRenewalAbandoned(this._logger, this._lease.JobId, consecutiveFailures);
                    await this._leaseLost.CancelAsync();
                    return;
                }
            }
        }
    }

    /// <summary>
    ///     The wait before the next renewal attempt: the configured interval brought forward by a random
    ///     fraction so a restarted fleet spreads out, and backed off exponentially while attempts are failing
    ///     without ever reaching beyond the lease itself.
    /// </summary>
    private TimeSpan NextDelay(int consecutiveFailures)
    {
        var interval = this._options.HeartbeatInterval;
        if (consecutiveFailures > 0)
        {
            var backoffFactor = Math.Pow(2, Math.Min(consecutiveFailures, 4));
            var backoff = interval * backoffFactor;
            var ceiling = this._options.LeaseDuration / 2;
            return backoff > ceiling ? ceiling : backoff;
        }

        var jitter = interval * this._options.HeartbeatJitterFraction * Random.Shared.NextDouble();
        return interval - jitter;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Review job {JobId} (lease generation {Generation}) told to stop: {Reason}")]
    private static partial void LogStopDirective(
        ILogger logger,
        Guid jobId,
        int generation,
        ReviewJobStopReason reason);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Review job {JobId} lease renewal failed ({ConsecutiveFailures} consecutive)")]
    private static partial void LogRenewalFailed(ILogger logger, Guid jobId, int consecutiveFailures, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message =
            "Review job {JobId} lease renewal failed {ConsecutiveFailures} times in a row; stopping execution so the lease can expire and the job be reclaimed")]
    private static partial void LogRenewalAbandoned(ILogger logger, Guid jobId, int consecutiveFailures);
}

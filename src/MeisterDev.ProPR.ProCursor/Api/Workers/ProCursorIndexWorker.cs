// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using MeisterDev.ProPR.ProCursor.Core;
using MeisterDev.ProPR.ProCursor.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.ProCursor.Workers;

/// <summary>
///     Background worker that polls the durable ProCursor index queue.
/// </summary>
public sealed partial class ProCursorIndexWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<ProCursorOptions> options,
    ILogger<ProCursorIndexWorker> logger) : BackgroundService
{
    private readonly Dictionary<Guid, ActiveJob> _activeJobsBySource = [];
    private readonly Lock _activeJobsLock = new();
    private readonly ProCursorOptions _options = options.Value;
    private bool _coordinatorResolutionFailureLogged;
    private bool _schedulerResolutionFailureLogged;

    /// <summary>
    ///     Whether the worker loop has entered its running state.
    /// </summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    ///     When the current or most recent worker cycle started.
    /// </summary>
    public DateTimeOffset? LastCycleStartedAt { get; private set; }

    /// <summary>
    ///     When the most recent worker cycle completed.
    /// </summary>
    public DateTimeOffset? LastCycleCompletedAt { get; private set; }

    /// <summary>
    ///     Number of source-scoped indexing jobs currently running.
    /// </summary>
    public int ActiveJobCount
    {
        get
        {
            lock (this._activeJobsLock)
            {
                return this._activeJobsBySource.Count;
            }
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = Math.Max(1, this._options.RefreshPollSeconds);
        LogWorkerStarted(logger, intervalSeconds);
        this.IsRunning = true;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));

        try
        {
            await this.RunCycleAsync(stoppingToken);

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await this.RunCycleAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            this.IsRunning = false;
            await this.WaitForActiveJobsAsync();
            LogWorkerStopped(logger);
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        try
        {
            this.LastCycleStartedAt = DateTimeOffset.UtcNow;

            using (var scheduleScope = scopeFactory.CreateScope())
            {
                var scheduler = this.TryResolveScheduler(scheduleScope.ServiceProvider);
                if (scheduler is not null)
                {
                    await scheduler.ScheduleRefreshesAsync(ct);
                }
            }

            await this.RecoverExpiredJobsAsync(ct);
            await this.StartPendingJobsAsync(ct);
            this.LastCycleCompletedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCycleError(logger, ex);
        }
    }

    /// <summary>How long the lease on a running job lasts after its claim or its last renewal.</summary>
    private TimeSpan LeaseDuration => TimeSpan.FromSeconds(this._options.IndexJobLeaseSeconds);

    /// <summary>
    ///     How often the lease on a running job is renewed. Five renewals fall into one lease duration, so a renewal
    ///     that fails because the database cannot be reached does not let the lease expire.
    /// </summary>
    private TimeSpan LeaseRenewalInterval => this.LeaseDuration / 5;

    private async Task RecoverExpiredJobsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var coordinator = this.TryResolveCoordinator(scope.ServiceProvider);
        if (coordinator is not null)
        {
            await coordinator.RecoverExpiredJobsAsync(this.GetActiveJobIds(), ct);
        }
    }

    private async Task StartPendingJobsAsync(CancellationToken ct)
    {
        while (this.ActiveJobCount < Math.Max(1, this._options.MaxIndexConcurrency))
        {
            using var scope = scopeFactory.CreateScope();
            var coordinator = this.TryResolveCoordinator(scope.ServiceProvider);
            if (coordinator is null)
            {
                return;
            }

            var excludedSourceIds = this.GetActiveSourceIds();
            var job = await coordinator.TryStartNextJobAsync(excludedSourceIds, ct);
            if (job is null)
            {
                return;
            }

            var startSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var jobTask = this.ExecuteJobAsync(job.Id, job.AttemptCount, job.KnowledgeSourceId, startSignal.Task);
            this.TrackActiveJob(job.KnowledgeSourceId, new ActiveJob(job.Id, jobTask));
            startSignal.TrySetResult();
        }
    }

    /// <summary>
    ///     Runs one claimed job and renews its lease until the job ends. The job is stopped when a renewal finds that
    ///     the job was cancelled, or that another service recovered and claimed it again.
    /// </summary>
    /// <param name="jobId">The claimed job.</param>
    /// <param name="attemptCount">The attempt count of the claim.</param>
    /// <param name="sourceId">The knowledge source of the job.</param>
    /// <param name="startSignal">Completes once the job is tracked as active.</param>
    private async Task ExecuteJobAsync(Guid jobId, int attemptCount, Guid sourceId, Task startSignal)
    {
        await startSignal;

        using var leaseLost = new CancellationTokenSource();
        using var jobEnded = new CancellationTokenSource();
        var renewal = this.RenewLeaseAsync(jobId, attemptCount, leaseLost, jobEnded.Token);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var coordinator = scope.ServiceProvider.GetRequiredService<ProCursorIndexCoordinator>();
            await coordinator.ExecuteJobAsync(jobId, attemptCount, leaseLost.Token);
        }
        catch (OperationCanceledException) when (leaseLost.IsCancellationRequested)
        {
            LogJobStoppedWithoutLease(logger, jobId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogJobEndUnrecorded(logger, jobId, ex);
        }
        finally
        {
            await jobEnded.CancelAsync();
            await renewal;
            this.UntrackActiveJob(sourceId);
        }
    }

    /// <summary>
    ///     Renews the lease on a running job until the job ends or the lease is lost. When renewals fail, the job is
    ///     stopped before its lease expires, because another service can then recover the job and run it again.
    /// </summary>
    /// <param name="jobId">The running job.</param>
    /// <param name="attemptCount">The attempt count of the claim.</param>
    /// <param name="leaseLost">Cancelled when the job is no longer held by this claim.</param>
    /// <param name="jobEnded">Cancelled when the job has ended.</param>
    private async Task RenewLeaseAsync(
        Guid jobId,
        int attemptCount,
        CancellationTokenSource leaseLost,
        CancellationToken jobEnded)
    {
        using var timer = new PeriodicTimer(this.LeaseRenewalInterval);

        // The lease was set at the claim, shortly before this loop starts, and each successful renewal extends it.
        var sinceLease = Stopwatch.StartNew();
        try
        {
            while (await timer.WaitForNextTickAsync(jobEnded))
            {
                // A renewal that is still running when the job has to stop counts as failed, so a database request that
                // hangs cannot keep the job running past its lease.
                using var renewal = CancellationTokenSource.CreateLinkedTokenSource(jobEnded);
                renewal.CancelAfter(Max(TimeSpan.Zero, this.LeaseDuration - this.LeaseRenewalInterval - sinceLease.Elapsed));

                // The coordinator sets the renewed lease while the request runs, so the age of the renewed lease counts
                // from the start of the request, including the time the database takes to answer.
                var sinceRequest = Stopwatch.StartNew();
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var coordinator = scope.ServiceProvider.GetRequiredService<ProCursorIndexCoordinator>();
                    if (!await coordinator.RenewLeaseAsync(jobId, attemptCount, renewal.Token))
                    {
                        await leaseLost.CancelAsync();
                        return;
                    }

                    sinceLease = sinceRequest;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !jobEnded.IsCancellationRequested)
                {
                    LogLeaseRenewalFailed(logger, jobId, ex);

                    // The next renewal would come after the lease has expired.
                    if (sinceLease.Elapsed + this.LeaseRenewalInterval >= this.LeaseDuration)
                    {
                        LogLeaseUnrenewable(logger, jobId);
                        await leaseLost.CancelAsync();
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The job has ended.
        }
    }

    private static TimeSpan Max(TimeSpan first, TimeSpan second)
    {
        return first > second ? first : second;
    }

    private ProCursorRefreshScheduler? TryResolveScheduler(IServiceProvider serviceProvider)
    {
        try
        {
            var scheduler = serviceProvider.GetService<ProCursorRefreshScheduler>();
            if (scheduler is not null)
            {
                this._schedulerResolutionFailureLogged = false;
            }

            return scheduler;
        }
        catch (InvalidOperationException ex)
        {
            if (!this._schedulerResolutionFailureLogged)
            {
                LogServiceGraphUnavailable(logger, nameof(ProCursorRefreshScheduler), ex);
                this._schedulerResolutionFailureLogged = true;
            }

            return null;
        }
    }

    private ProCursorIndexCoordinator? TryResolveCoordinator(IServiceProvider serviceProvider)
    {
        try
        {
            var coordinator = serviceProvider.GetService<ProCursorIndexCoordinator>();
            if (coordinator is not null)
            {
                this._coordinatorResolutionFailureLogged = false;
            }

            return coordinator;
        }
        catch (InvalidOperationException ex)
        {
            if (!this._coordinatorResolutionFailureLogged)
            {
                LogServiceGraphUnavailable(logger, nameof(ProCursorIndexCoordinator), ex);
                this._coordinatorResolutionFailureLogged = true;
            }

            return null;
        }
    }

    private Guid[] GetActiveSourceIds()
    {
        lock (this._activeJobsLock)
        {
            return this._activeJobsBySource.Keys.ToArray();
        }
    }

    private Guid[] GetActiveJobIds()
    {
        lock (this._activeJobsLock)
        {
            return this._activeJobsBySource.Values.Select(job => job.JobId).ToArray();
        }
    }

    private void TrackActiveJob(Guid sourceId, ActiveJob job)
    {
        lock (this._activeJobsLock)
        {
            this._activeJobsBySource[sourceId] = job;
        }
    }

    private void UntrackActiveJob(Guid sourceId)
    {
        lock (this._activeJobsLock)
        {
            this._activeJobsBySource.Remove(sourceId);
        }
    }

    private async Task WaitForActiveJobsAsync()
    {
        Task[] activeTasks;
        lock (this._activeJobsLock)
        {
            activeTasks = this._activeJobsBySource.Values.Select(job => job.Execution).ToArray();
        }

        if (activeTasks.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(activeTasks);
        }
        catch (OperationCanceledException)
        {
            // Shutdown canceled the remaining jobs.
        }
    }

    /// <summary>A job that this worker runs.</summary>
    /// <param name="JobId">The job.</param>
    /// <param name="Execution">The task that runs the job and renews its lease.</param>
    private sealed record ActiveJob(Guid JobId, Task Execution);
}

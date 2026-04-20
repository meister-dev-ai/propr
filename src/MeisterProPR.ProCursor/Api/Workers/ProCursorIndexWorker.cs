// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Options;
using MeisterProPR.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Api.Workers;

/// <summary>
///     Background worker that polls the durable ProCursor index queue.
/// </summary>
public sealed partial class ProCursorIndexWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<ProCursorOptions> options,
    ILogger<ProCursorIndexWorker> logger) : BackgroundService
{
    private readonly Dictionary<Guid, Task> _activeJobsBySource = [];
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

            await this.StartPendingJobsAsync(ct);
            this.LastCycleCompletedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCycleError(logger, ex);
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
            var jobTask = this.ExecuteJobAsync(job.Id, job.KnowledgeSourceId, CancellationToken.None, startSignal.Task);
            this.TrackActiveJob(job.KnowledgeSourceId, jobTask);
            startSignal.TrySetResult();
        }
    }

    private async Task ExecuteJobAsync(Guid jobId, Guid sourceId, CancellationToken ct, Task startSignal)
    {
        await startSignal;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var coordinator = scope.ServiceProvider.GetRequiredService<ProCursorIndexCoordinator>();
            await coordinator.ExecuteJobAsync(jobId, ct);
        }
        finally
        {
            this.UntrackActiveJob(sourceId);
        }
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

    private void TrackActiveJob(Guid sourceId, Task jobTask)
    {
        lock (this._activeJobsLock)
        {
            this._activeJobsBySource[sourceId] = jobTask;
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
            activeTasks = this._activeJobsBySource.Values.ToArray();
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
}

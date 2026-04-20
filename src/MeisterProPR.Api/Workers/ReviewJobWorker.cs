// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;
using System.Diagnostics;
using MeisterProPR.Api.Telemetry;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Api.Workers;

/// <summary>Background worker that pulls pending jobs and runs reviews.</summary>
public sealed partial class ReviewJobWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<WorkerOptions> workerOptions,
    ReviewJobMetrics metrics,
    ILogger<ReviewJobWorker> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, Task> _inflight = new();
    private DateTimeOffset _lastCleanupAt = DateTimeOffset.MinValue;
    private TaskCompletionSource _startedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>True while the worker loop is active.</summary>
    public bool IsRunning { get; private set; }

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        this._startedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await base.StartAsync(cancellationToken);
        await this._startedSignal.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Main loop that polls for pending jobs and schedules processing.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        this.IsRunning = true;
        LogWorkerStarted(logger);
        this._startedSignal.TrySetResult();

        // Clean up any jobs left stuck in Processing from a previous run before entering the main loop.
        await this.CleanUpStuckJobsAsync(CancellationToken.None);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(workerOptions.Value.PollIntervalMilliseconds));

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
        }
        finally
        {
            this.IsRunning = false;
            if (this._inflight.Count > 0)
            {
                try
                {
                    await Task.WhenAll(this._inflight.Values);
                }
                catch (Exception ex)
                {
                    LogShutdownDrainError(logger, ex);
                }
            }

            LogWorkerStopped(logger);
        }
    }

    private async Task RunCycleAsync(CancellationToken stoppingToken)
    {
        // Periodic stuck-job cleanup every 10 minutes.
        if (DateTimeOffset.UtcNow - this._lastCleanupAt >= TimeSpan.FromMinutes(10))
        {
            await this.CleanUpStuckJobsAsync(stoppingToken);
        }

        using var tickScope = scopeFactory.CreateScope();
        var jobRepository = tickScope.ServiceProvider.GetRequiredService<IReviewJobExecutionStore>();

        foreach (var job in jobRepository.GetPendingJobs())
        {
            if (!await jobRepository.TryTransitionAsync(job.Id, JobStatus.Pending, JobStatus.Processing, stoppingToken))
            {
                continue;
            }

            var capturedJob = job;
            var task = this.ProcessJobSafeAsync(capturedJob, stoppingToken);
            this._inflight[capturedJob.Id] = task;
            _ = task.ContinueWith(
                t => this._inflight.TryRemove(capturedJob.Id, out _),
                TaskScheduler.Default);
        }
    }

    /// <summary>Processes a single job safely, handling exceptions and cancellations.</summary>
    private async Task ProcessJobSafeAsync(ReviewJob job, CancellationToken stoppingToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var outcome = "completed";
        using var activity = ReviewJobTelemetry.StartActivity(
            "review_job.process",
            provider: job.Provider,
            clientId: job.ClientId);
        activity?.SetTag("review_job.id", job.Id.ToString("D"));
        activity?.SetTag("review_job.pull_request_id", job.PullRequestId);
        activity?.SetTag("review_job.iteration_id", job.IterationId);

        using var logScope = logger.BeginScope(
            new Dictionary<string, object?>
            {
                [ReviewJobTelemetry.ScmProviderTagName] = ReviewJobTelemetry.ToProviderTag(job.Provider),
                [ReviewJobTelemetry.ClientIdTagName] = job.ClientId.ToString("D"),
                ["review_job_id"] = job.Id.ToString("D"),
            });

        using var scope = scopeFactory.CreateScope();
        var jobRepository = scope.ServiceProvider.GetRequiredService<IReviewJobExecutionStore>();

        try
        {
            var orchestrator = scope.ServiceProvider.GetRequiredService<IReviewJobProcessor>();
            await orchestrator.ProcessAsync(job, stoppingToken);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            await jobRepository.TryTransitionAsync(job.Id, JobStatus.Processing, JobStatus.Pending, stoppingToken);
        }
        catch (Exception ex)
        {
            outcome = "failed";
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("error.type", ex.GetType().FullName ?? ex.GetType().Name);
            LogJobProcessingError(logger, job.Id, ex);
            await jobRepository.SetFailedAsync(job.Id, ex.Message, stoppingToken);
        }
        finally
        {
            stopwatch.Stop();
            activity?.SetTag("review_job.outcome", outcome);
            metrics.RecordJobDuration(job.Provider, stopwatch.Elapsed.TotalSeconds, outcome);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "ReviewJobWorker started")]
    private static partial void LogWorkerStarted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "ReviewJobWorker stopped")]
    private static partial void LogWorkerStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ReviewJobWorker: error during shutdown drain")]
    private static partial void LogShutdownDrainError(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "ReviewJobWorker: unhandled exception processing job {JobId}")]
    private static partial void LogJobProcessingError(ILogger logger, Guid jobId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "ReviewJobWorker: transitioning stuck job {JobId} (in Processing since {ProcessingStartedAt}) to Failed")]
    private static partial void LogStuckJobDetected(ILogger logger, Guid jobId, DateTimeOffset? processingStartedAt);

    /// <summary>Finds jobs stuck in <c>Processing</c> and marks them <c>Failed</c>.</summary>
    private async Task CleanUpStuckJobsAsync(CancellationToken ct)
    {
        this._lastCleanupAt = DateTimeOffset.UtcNow;
        var timeout = TimeSpan.FromMinutes(workerOptions.Value.StuckJobTimeoutMinutes);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var jobRepository = scope.ServiceProvider.GetRequiredService<IReviewJobExecutionStore>();
            var stuckJobs = await jobRepository.GetStuckProcessingJobsAsync(timeout, ct);
            foreach (var job in stuckJobs)
            {
                LogStuckJobDetected(logger, job.Id, job.ProcessingStartedAt);
                await jobRepository.SetFailedAsync(
                    job.Id,
                    $"Job was stuck in Processing state for more than {workerOptions.Value.StuckJobTimeoutMinutes} minutes and was automatically transitioned to Failed.",
                    ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ReviewJobWorker: error during stuck-job cleanup");
        }
    }
}

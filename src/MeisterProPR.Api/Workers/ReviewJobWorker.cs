// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Collections.Concurrent;
using System.Diagnostics;
using MeisterProPR.Api.Telemetry;
using MeisterProPR.Application.Features.Budgeting;
using MeisterProPR.Application.Features.Budgeting.Models;
using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Application.Features.Licensing.Ports;
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
    IReviewJobCancellationRegistry cancellationRegistry,
    ILogger<ReviewJobWorker> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, byte> _claimed = new();
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
            // Expected when the host cancels the stopping token during shutdown; the finally block below drains in-flight work.
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
        var licensingCapabilityService = tickScope.ServiceProvider.GetService<ILicensingCapabilityService>();
        var parallelReviewExecutionEnabled = licensingCapabilityService is null
                                             || await licensingCapabilityService.IsEnabledAsync(PremiumCapabilityKey.ParallelReviewExecution, stoppingToken);
        var maxConcurrentReviewJobs = workerOptions.Value.MaxConcurrentReviewJobs;
        var budgetCapsProvider = tickScope.ServiceProvider.GetService<IBudgetCapsProvider>();
        var spendAccumulator = tickScope.ServiceProvider.GetService<IReviewSpendAccumulator>();
        var budgetEventPublisher = tickScope.ServiceProvider.GetService<IBudgetEventPublisher>();

        foreach (var job in jobRepository.GetPendingJobs())
        {
            if (!parallelReviewExecutionEnabled)
            {
                var processingJobCount = await jobRepository.CountProcessingJobsAsync(stoppingToken);
                if (processingJobCount > 0)
                {
                    break;
                }
            }
            else if (this._inflight.Count >= maxConcurrentReviewJobs)
            {
                // Bounded parallelism: cap how many reviews run at once so a burst of pending jobs
                // cannot fan out into an unbounded memory/CPU multiplier. The overflow stays Pending
                // and is claimed on later cycles as in-flight work drains below the cap.
                break;
            }

            if (budgetCapsProvider is not null && spendAccumulator is not null)
            {
                var breach = await EvaluateAdmissionBreachAsync(budgetCapsProvider, spendAccumulator, job, stoppingToken);
                if (breach is not null)
                {
                    // A soft or hard cap is already reached, so this new review is held rather than started. It
                    // runs only when an operator restarts it after freeing budget — there is no automatic resume.
                    await jobRepository.SetBudgetHeldAsync(job.Id, breach.Scope, breach.CapKind, breach.ThresholdUsd, breach.SpentUsd, stoppingToken);
                    if (budgetEventPublisher is not null)
                    {
                        await budgetEventPublisher.PublishAsync(
                            BudgetEventNotification.FromBreach(breach, job.ClientId, job.Id, job.PullRequestId, job.IterationId),
                            stoppingToken);
                    }

                    continue;
                }
            }

            if (!await jobRepository.TryTransitionAsync(job.Id, JobStatus.Pending, JobStatus.Processing, stoppingToken))
            {
                continue;
            }

            var capturedJob = job;
            this._claimed[capturedJob.Id] = 0;
            var task = this.ProcessJobSafeAsync(capturedJob, stoppingToken);
            this._inflight[capturedJob.Id] = task;
            _ = task.ContinueWith(
                t =>
                {
                    this._inflight.TryRemove(capturedJob.Id, out _);
                    this._claimed.TryRemove(capturedJob.Id, out _);
                },
                TaskScheduler.Default);
        }
    }

    private static async Task<BudgetBreach?> EvaluateAdmissionBreachAsync(
        IBudgetCapsProvider budgetCapsProvider,
        IReviewSpendAccumulator spendAccumulator,
        ReviewJob job,
        CancellationToken ct)
    {
        var caps = await budgetCapsProvider.GetCapsAsync(job.ClientId, ct);
        if (!caps.AnyConfigured)
        {
            return null;
        }

        var baseline = await spendAccumulator.GetBaselineAsync(job, DateOnly.FromDateTime(DateTime.UtcNow), ct);
        return BudgetEvaluator.FindAdmissionBreach(
            caps,
            baseline.ClientMonthToDate.KnownUsd,
            baseline.PullRequest.KnownUsd,
            baseline.Increment.KnownUsd);
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

        // Register a per-job cancellation source and run the review under a token linked to it and the
        // host-shutdown token. A manual stop cancels the former; shutdown cancels the latter — the catch
        // below tells them apart so an operator-halted job is finalized as Stopped rather than requeued.
        var jobCancellationToken = cancellationRegistry.Register(job.Id);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, jobCancellationToken);

        try
        {
            var orchestrator = scope.ServiceProvider.GetRequiredService<IReviewJobProcessor>();
            await orchestrator.ProcessAsync(job, linkedCts.Token);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (OperationCanceledException)
        {
            if (jobCancellationToken.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                // Manual stop by a client administrator: the endpoint already persisted Stopped. Ensure it
                // and do NOT reset to Pending — that would resurrect a job the operator explicitly halted.
                outcome = "stopped";
                LogJobStoppedByOperator(logger, job.Id);
                await jobRepository.SetStoppedAsync(job.Id, CancellationToken.None);
            }
            else
            {
                outcome = "cancelled";
                await jobRepository.TryTransitionAsync(job.Id, JobStatus.Processing, JobStatus.Pending, stoppingToken);
            }
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
            cancellationRegistry.Remove(job.Id);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "ReviewJobWorker: job {JobId} stopped by client administrator")]
    private static partial void LogJobStoppedByOperator(ILogger logger, Guid jobId);

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
                if (this._claimed.ContainsKey(job.Id))
                {
                    continue;
                }

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

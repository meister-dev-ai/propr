using System.Collections.Concurrent;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Services;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Api.Workers;

/// <summary>Background worker that pulls pending jobs and runs reviews.</summary>
public sealed partial class ReviewJobWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<ReviewJobWorker> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, Task> _inflight = new();

    /// <summary>True while the worker loop is active.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Main loop that polls for pending jobs and schedules processing.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        this.IsRunning = true;
        LogWorkerStarted(logger);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                using var tickScope = scopeFactory.CreateScope();
                var jobRepository = tickScope.ServiceProvider.GetRequiredService<IJobRepository>();

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

    /// <summary>Processes a single job safely, handling exceptions and cancellations.</summary>
    private async Task ProcessJobSafeAsync(ReviewJob job, CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var jobRepository = scope.ServiceProvider.GetRequiredService<IJobRepository>();

        try
        {
            var orchestrator = scope.ServiceProvider.GetRequiredService<ReviewOrchestrationService>();
            await orchestrator.ProcessAsync(job, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            await jobRepository.TryTransitionAsync(job.Id, JobStatus.Processing, JobStatus.Pending, stoppingToken);
        }
        catch (Exception ex)
        {
            LogJobProcessingError(logger, job.Id, ex);
            await jobRepository.SetFailedAsync(job.Id, ex.Message, stoppingToken);
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
}

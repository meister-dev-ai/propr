using System.Collections.Concurrent;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Services;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Api.Workers;

/// <summary>Background worker that pulls pending jobs and runs reviews.</summary>
public sealed class ReviewJobWorker(
    IJobRepository jobRepository,
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
        logger.LogInformation("ReviewJobWorker started.");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (var job in jobRepository.GetPendingJobs())
                {
                    if (!jobRepository.TryTransition(job.Id, JobStatus.Pending, JobStatus.Processing))
                    {
                        continue;
                    }

                    var capturedJob = job;
                    var task = this.ProcessJobSafeAsync(capturedJob, stoppingToken);
                    this._inflight[capturedJob.Id] = task;
                    _ = task.ContinueWith(
                        _ => this._inflight.TryRemove(capturedJob.Id, out _),
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
                    logger.LogWarning(ex, "Error during shutdown drain.");
                }
            }

            logger.LogInformation("ReviewJobWorker stopped.");
        }
    }

    /// <summary>Processes a single job safely, handling exceptions and cancellations.</summary>
    private async Task ProcessJobSafeAsync(ReviewJob job, CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<ReviewOrchestrationService>();
            await orchestrator.ProcessAsync(job, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            jobRepository.TryTransition(job.Id, JobStatus.Processing, JobStatus.Pending);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception processing job {JobId}", job.Id);
            jobRepository.SetFailed(job.Id, ex.Message);
        }
    }
}
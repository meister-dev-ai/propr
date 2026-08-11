// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Collections.Concurrent;
using System.Diagnostics;
using MeisterDev.ProPR.Api.Telemetry;
using MeisterDev.ProPR.Application.Features.Budgeting;
using MeisterDev.ProPR.Application.Features.Budgeting.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Api.Workers;

/// <summary>Background worker that pulls pending jobs and runs reviews.</summary>
public sealed partial class ReviewJobWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<WorkerOptions> workerOptions,
    IOptions<ReviewLeaseOptions> leaseOptions,
    ReviewJobMetrics metrics,
    IReviewJobCancellationRegistry cancellationRegistry,
    TimeProvider timeProvider,
    ILogger<ReviewJobWorker> logger,
    // Optional: an installation with no database never dispatches to a runner, and the offline harness
    // composes this worker without the runner surface at all.
    IRunnerJobBudgetRegistry? budgets = null,
    IRunnerWorkspaceRegistry? workspaceRegistry = null,
    IRunnerJobToolsRegistry? toolsRegistry = null,
    RunnerRelayReplayCache? relayReplays = null,
    RunnerSubmissionLedger? submissions = null) : BackgroundService
{
    /// <summary>
    ///     Identity this process stamps on the leases it holds. Machine name and process id together are
    ///     enough to tell two replicas apart, and enough for an operator to find the host holding a job.
    /// </summary>
    private static readonly string LeaseOwnerIdentity =
        $"{Environment.MachineName}:{Environment.ProcessId}";

    private readonly ConcurrentDictionary<Guid, Task> _inflight = new();
    private DateTimeOffset _lastReclaimSweepAt = DateTimeOffset.MinValue;
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

        if (workerOptions.Value.RetiredStuckJobTimeoutMinutes is { } retiredTimeout)
        {
            LogRetiredStuckJobTimeoutIgnored(logger, retiredTimeout);
        }

        // Take back anything this or another host abandoned before entering the main loop. A job left
        // processing by a host that died is identified by its expired lease, not by how long it has been
        // running, so a review that is simply long is never touched.
        await this.ReclaimExpiredLeasesAsync(CancellationToken.None);

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
        if (DateTimeOffset.UtcNow - this._lastReclaimSweepAt >= leaseOptions.Value.ReclaimSweepInterval)
        {
            await this.ReclaimExpiredLeasesAsync(stoppingToken);
        }

        using var tickScope = scopeFactory.CreateScope();
        var jobRepository = tickScope.ServiceProvider.GetRequiredService<IReviewJobExecutionStore>();
        var leaseStore = tickScope.ServiceProvider.GetRequiredService<IReviewJobLeaseStore>();
        var licensingCapabilityService = tickScope.ServiceProvider.GetService<ILicensingCapabilityService>();
        var parallelReviewExecutionEnabled = licensingCapabilityService is null
                                             || await licensingCapabilityService.IsEnabledAsync(PremiumCapabilityKey.ParallelReviewExecution, stoppingToken);
        // Without the parallel-execution capability a replica runs one job at a time whatever the configured
        // cap says. The fleet-wide check below is what keeps that true across replicas; this clamp is what
        // keeps it true within one.
        var maxConcurrentReviewJobs = ReviewConcurrencyPolicy.Effective(
            workerOptions.Value.MaxConcurrentReviewJobs,
            parallelReviewExecutionEnabled);
        // Where a review may run is decided per candidate below rather than here, because runners are
        // scoped to clients and the installation is not. One tenant running runners must not stop every
        // other tenant's reviews: those jobs can never be offered to a runner outside their tenant, so
        // suppressing them here too would leave them pending forever while the fleet looks healthy.
        // Read once per tick so every candidate is judged against the same snapshot.
        var fleetMonitor = tickScope.ServiceProvider.GetService<IRunnerFleetMonitor>();
        var fleet = fleetMonitor is null ? null : await fleetMonitor.GetStatusAsync(stoppingToken);
        if (fleet?.Stall is not null)
        {
            LogQueueStalled(
                logger,
                fleet.Stall.Cause.ToString(),
                fleet.Stall.PendingJobCount,
                fleet.Stall.OldestPendingSince,
                fleet.Stall.Detail ?? string.Empty);
        }

        var budgetCapsProvider = tickScope.ServiceProvider.GetService<IBudgetCapsProvider>();
        var spendAccumulator = tickScope.ServiceProvider.GetService<IReviewSpendAccumulator>();
        var budgetEventPublisher = tickScope.ServiceProvider.GetService<IBudgetEventPublisher>();
        var clientRegistry = tickScope.ServiceProvider.GetService<IClientRegistry>();

        // Whether a client's pass list forces in-process execution, read once per client per tick: the
        // same client tends to fill a window with many jobs.
        var requiresInProcessByClient = new Dictionary<Guid, bool>();

        // The window is bounded, and the fleet skip can fill it entirely with jobs this process must
        // leave for runners. Paging past such a window is what keeps one tenant's runner-backed backlog
        // from starving a tenant with no runner at all: a single window simply never reached their jobs.
        DateTimeOffset? windowCursor = null;
        for (var window = 0; window < MaxClaimWindowsPerCycle; window++)
        {
            var candidates = await leaseStore.GetClaimCandidatesAsync(
                leaseOptions.Value.ClaimCandidateLimit,
                windowCursor,
                stoppingToken);
            if (candidates.Count == 0)
            {
                break;
            }

            var everyCandidateLeftForRunners = true;
            var stopScanning = false;
            foreach (var job in candidates)
            {
                // A job a runner could take is left for a runner. There is no in-process fallback,
                // because an unreported fallback would break the isolation guarantee on the installations
                // that depend on it. A job no active runner is eligible for is still run here.
                if (fleet is not null && !fleet.MayExecuteInProcess(job.ClientId))
                {
                    // One deliberate exception: a publishing pr_wide pass never dispatches to a runner,
                    // so leaving such a job for a runner would leave it un-reviewed while every lease
                    // offer refuses its manifest. Running it here is recorded in the log, because an
                    // operator relying on runner isolation should see each review that ran locally.
                    if (!await RequiresInProcessExecutionAsync(job.ClientId))
                    {
                        continue;
                    }

                    LogPrWideRunsInProcess(logger, job.Id, job.ClientId);
                }

                everyCandidateLeftForRunners = false;

                if (this._inflight.Count >= maxConcurrentReviewJobs)
                {
                    // Bounded parallelism: cap how many reviews run at once so a burst of pending jobs
                    // cannot fan out into an unbounded memory/CPU multiplier. The overflow stays Pending
                    // and is claimed on later cycles as in-flight work drains below the cap. Without the
                    // parallel-execution capability the cap is one, so this is also what holds an
                    // unlicensed replica to a single review.
                    stopScanning = true;
                    break;
                }

                if (!parallelReviewExecutionEnabled)
                {
                    // One review at a time has to mean across every host sharing the database, not just
                    // this one, so what decides it is the database's count rather than this replica's.
                    var processingJobCount = await jobRepository.CountProcessingJobsAsync(stoppingToken);
                    if (processingJobCount > 0)
                    {
                        stopScanning = true;
                        break;
                    }
                }

                if (budgetCapsProvider is not null && spendAccumulator is not null)
                {
                    var breach = await EvaluateAdmissionBreachAsync(budgetCapsProvider, spendAccumulator, job, stoppingToken);
                    if (breach is not null)
                    {
                        // A soft or hard cap is already reached, so this new review is held rather than
                        // started. It runs only when an operator restarts it after freeing budget. There is
                        // no automatic resume.
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

                // The claim stamps the lease in the same statement that moves the status, so a loser here simply
                // gets no lease back and moves on to the next candidate.
                var lease = await leaseStore.TryClaimAsync(
                    job.Id,
                    LeaseOwnerIdentity,
                    leaseOptions.Value.LeaseDuration,
                    stoppingToken);
                if (lease is null)
                {
                    continue;
                }

                var capturedJob = job;
                var task = this.ProcessJobSafeAsync(capturedJob, lease, stoppingToken);
                this._inflight[capturedJob.Id] = task;
                _ = task.ContinueWith(
                    t => this._inflight.TryRemove(capturedJob.Id, out _),
                    TaskScheduler.Default);
            }

            // Deeper windows are only for the starvation case. A window with anything claimable in it is
            // this cycle's work; the next cycle starts from the front again.
            if (stopScanning || !everyCandidateLeftForRunners || candidates.Count < leaseOptions.Value.ClaimCandidateLimit)
            {
                break;
            }

            windowCursor = candidates[^1].SubmittedAt;
        }

        async Task<bool> RequiresInProcessExecutionAsync(Guid clientId)
        {
            if (clientRegistry is null)
            {
                return false;
            }

            if (requiresInProcessByClient.TryGetValue(clientId, out var known))
            {
                return known;
            }

            var requires = false;
            try
            {
                var passes = await clientRegistry.GetReviewPassesAsync(clientId, stoppingToken);
                requires = passes.Any(pass =>
                    !pass.Shadow && string.Equals(pass.Scope, ReviewPassScope.PrWide, StringComparison.Ordinal));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Unreadable configuration keeps the current behaviour of leaving the job for a runner,
                // rather than turning a failed configuration read into a worker crash or an unexpected
                // local execution.
                LogPassListUnreadable(logger, clientId, ex);
            }

            requiresInProcessByClient[clientId] = requires;
            return requires;
        }
    }

    /// <summary>
    ///     How many claim windows one cycle may page through when every candidate so far was left for a
    ///     runner. Bounds the sweep the way the window bounds a single read.
    /// </summary>
    private const int MaxClaimWindowsPerCycle = 20;

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

        var baseline = await spendAccumulator.GetBaselineAsync(
            ReviewSpendSubject.For(job),
            DateOnly.FromDateTime(DateTime.UtcNow),
            ct);
        return BudgetEvaluator.FindAdmissionBreach(
            caps,
            baseline.ClientMonthToDate.KnownUsd,
            baseline.PullRequest.KnownUsd,
            baseline.Increment.KnownUsd);
    }

    /// <summary>Processes a single job safely, handling exceptions and cancellations.</summary>
    private async Task ProcessJobSafeAsync(ReviewJob job, ReviewJobLease lease, CancellationToken stoppingToken)
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

        // The heartbeat gets its own scope. It renews on a timer while the pipeline is working, and the two
        // would otherwise be using one scoped database context from two threads at once.
        using var heartbeatScope = scopeFactory.CreateScope();
        var leaseStore = heartbeatScope.ServiceProvider.GetRequiredService<IReviewJobLeaseStore>();

        // Register a per-job cancellation source and run the review under a token linked to it, the
        // host-shutdown token, and loss of the lease. A manual stop cancels the first; shutdown cancels the
        // second; the third fires when this host can no longer prove it still owns the job. The catches below
        // tell them apart, because each one finalizes the job differently.
        var jobCancellationToken = cancellationRegistry.Register(job.Id);
        await using var heartbeat = ReviewJobLeaseHeartbeat.Start(
            lease,
            leaseStore,
            leaseOptions.Value,
            timeProvider,
            logger,
            stoppingToken);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            jobCancellationToken,
            heartbeat.LeaseLost);

        try
        {
            var orchestrator = scope.ServiceProvider.GetRequiredService<IReviewJobProcessor>();
            await orchestrator.ProcessAsync(job, linkedCts.Token);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (OperationCanceledException)
        {
            // Stop renewing before writing anything, so nothing extends a lease this job is done with.
            await heartbeat.DisposeAsync();

            if (heartbeat.IsLeaseLost && !jobCancellationToken.IsCancellationRequested)
            {
                // The heartbeat is the only channel that reaches an execution wherever it runs, so a stop, a
                // supersede, and a budget cut all arrive through it. In every one of those cases the
                // persisted status already says what happened and writing anything here would overwrite it;
                // what the reason changes is how the outcome is attributed and reported.
                outcome = OutcomeFor(heartbeat.StopReason);
                LogJobStopDirective(logger, job.Id, lease.Generation, heartbeat.StopReason);
            }
            else if (jobCancellationToken.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                // Manual stop by a client administrator: the endpoint already persisted Stopped. Ensure it
                // and do NOT reset to Pending — that would resurrect a job the operator explicitly halted.
                outcome = "stopped";
                LogJobStoppedByOperator(logger, job.Id);
                await jobRepository.SetStoppedAsync(job.Id, CancellationToken.None);
            }
            else
            {
                // Host shutdown, drain, or scale-in. Handing the lease back deliberately returns the job to
                // the queue and is distinguishable from an expiry, so a planned restart is never charged
                // against the job as an abandonment.
                outcome = "released";
                await leaseStore.TryReleaseAsync(lease, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            await heartbeat.DisposeAsync();
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

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "WORKER_STUCK_JOB_TIMEOUT_MINUTES is set to {Minutes} and is ignored. Review jobs are now kept "
            + "alive by a lease rather than failed for their age; configure REVIEW_LEASE_DURATION_SECONDS "
            + "and REVIEW_LEASE_HEARTBEAT_INTERVAL_SECONDS instead.")]
    private static partial void LogRetiredStuckJobTimeoutIgnored(ILogger logger, int minutes);

    [LoggerMessage(Level = LogLevel.Information, Message = "ReviewJobWorker stopped")]
    private static partial void LogWorkerStopped(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "Review queue is stalled ({Cause}): {PendingJobCount} job(s) pending, oldest since "
            + "{OldestPendingSince}. {Detail}")]
    private static partial void LogQueueStalled(
        ILogger logger,
        string cause,
        int pendingJobCount,
        DateTimeOffset oldestPendingSince,
        string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ReviewJobWorker: error during shutdown drain")]
    private static partial void LogShutdownDrainError(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "ReviewJobWorker: unhandled exception processing job {JobId}")]
    private static partial void LogJobProcessingError(ILogger logger, Guid jobId, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "ReviewJobWorker: job {JobId} stopped by client administrator")]
    private static partial void LogJobStoppedByOperator(ILogger logger, Guid jobId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "ReviewJobWorker: stopped work on job {JobId} (lease generation {Generation}): {Reason}")]
    private static partial void LogJobStopDirective(
        ILogger logger,
        Guid jobId,
        int generation,
        ReviewJobStopReason reason);

    /// <summary>
    ///     The recorded outcome for each stop reason. Attribution matters: a review halted by an operator, a
    ///     review overtaken by a newer push, and one cut off by a budget are three different things, and
    ///     recording all of them as one hides which is happening.
    /// </summary>
    private static string OutcomeFor(ReviewJobStopReason reason)
    {
        return reason switch
        {
            ReviewJobStopReason.OperatorStop => "stopped",
            ReviewJobStopReason.Superseded => "superseded",
            ReviewJobStopReason.BudgetCapReached => "budget_exceeded",
            ReviewJobStopReason.RegistrationRevoked => "revoked",
            _ => "lease_lost",
        };
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "ReviewJobWorker: reclaimed job {JobId} after its lease expired at {ExpiredAt}")]
    private static partial void LogJobReclaimed(ILogger logger, Guid jobId, DateTimeOffset expiredAt);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "ReviewJobWorker: failed job {JobId}; it lost its lease more often than its reclaim budget allows")]
    private static partial void LogReclaimBudgetExhausted(ILogger logger, Guid jobId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "ReviewJobWorker: failed job {JobId}; publication began and did not finish within its timeout")]
    private static partial void LogPublicationTimedOut(ILogger logger, Guid jobId);

    /// <summary>
    ///     Takes back jobs whose lease expired, and fails those whose publication never finished.
    ///     <para>
    ///         This replaces failing jobs by age. Age cannot distinguish a long review from an abandoned
    ///         one, and the exemption that protected a running job was held in the memory of the process
    ///         running it, so a second host would fail another host's healthy review as soon as it crossed
    ///         the timeout. An expired lease states what age cannot: the lease is no longer being renewed.
    ///     </para>
    /// </summary>
    private async Task ReclaimExpiredLeasesAsync(CancellationToken ct)
    {
        this._lastReclaimSweepAt = DateTimeOffset.UtcNow;
        var options = leaseOptions.Value;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var leaseStore = scope.ServiceProvider.GetRequiredService<IReviewJobLeaseStore>();
            var fleetMetrics = scope.ServiceProvider.GetService<RunnerFleetMetrics>();

            var expired = await leaseStore.GetExpiredLeasesAsync(
                options.MaxReclaimsPerSweep,
                options.ReclaimBackoff,
                options.PublicationTimeout,
                ct);

            foreach (var lease in expired)
            {
                var outcome = await leaseStore.TryReclaimAsync(
                    lease,
                    options.MaxConsecutiveReclaims,
                    options.MaxTotalReclaims,
                    ct);

                // Counted whatever the outcome, including the case where another host claimed the job
                // first. A reclaim rate an operator can act on has to include the sweeps that found
                // nothing to do.
                fleetMetrics?.RecordReclaim(outcome);

                switch (outcome)
                {
                    case ReviewJobReclaimOutcome.Requeued:
                        // The previous holder is not returning to this job, so the budget scope this
                        // replica was holding open for it is released with the lease. Without this, a
                        // runner that stops mid-review leaves a scope behind for the life of the process.
                        budgets?.Release(lease.JobId);
                        LogJobReclaimed(logger, lease.JobId, lease.ExpiredAt);
                        break;
                    case ReviewJobReclaimOutcome.FailedOutOfReclaimBudget:
                        budgets?.Release(lease.JobId);
                        LogReclaimBudgetExhausted(logger, lease.JobId);
                        break;
                    case ReviewJobReclaimOutcome.NotReclaimed:
                    default:
                        // The holder recovered and renewed, or another host claimed the job first. Either
                        // way the job has an owner, so this sweep leaves it alone.
                        break;
                }
            }

            foreach (var jobId in await leaseStore.FailTimedOutPublicationsAsync(
                         options.MaxReclaimsPerSweep,
                         options.PublicationTimeout,
                         ct))
            {
                LogPublicationTimedOut(logger, jobId);
            }

            await this.ScrubAbandonedRunnerStateAsync(scope, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ReviewJobWorker: error during lease-expiry reclaim");
        }
    }

    /// <summary>
    ///     Releases the per-job state this replica still holds for jobs that are no longer executing.
    ///     <para>
    ///         The workspace, the budget scope, the tools, and the submission memory are all process-local,
    ///         and the ordinary release paths run only when the runner reports that it is done. A runner
    ///         that stops responding never reports: its job is reclaimed through the database by whichever
    ///         replica sweeps first, and the replica that served it would keep two full checkouts on disk
    ///         for that job indefinitely. Anything held for a job that is no longer Processing has no
    ///         legitimate caller left, because lease authorization refuses them, so what remains is only
    ///         disk and memory.
    ///     </para>
    /// </summary>
    private async Task ScrubAbandonedRunnerStateAsync(IServiceScope scope, CancellationToken ct)
    {
        if (workspaceRegistry is null)
        {
            return;
        }

        var heldJobIds = workspaceRegistry.RegisteredJobIds;
        if (heldJobIds.Count == 0)
        {
            return;
        }

        var jobRepository = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        foreach (var jobId in heldJobIds)
        {
            ct.ThrowIfCancellationRequested();

            var job = jobRepository.GetById(jobId);
            if (job is not null && job.Status == JobStatus.Processing)
            {
                continue;
            }

            await workspaceRegistry.ReleaseAsync(jobId);
            budgets?.Release(jobId);
            toolsRegistry?.Release(jobId);
            relayReplays?.Release(jobId);
            submissions?.Release(jobId);
            LogAbandonedRunnerStateScrubbed(logger, jobId, job?.Status.ToString() ?? "missing");
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Released this replica's workspace and per-job state for job {JobId}, whose status is {Status} and whose runner never released it")]
    private static partial void LogAbandonedRunnerStateScrubbed(ILogger logger, Guid jobId, string status);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Job {JobId} runs in process although client {ClientId} has an active runner: its pass list "
                  + "has a publishing pr_wide entry, which does not dispatch to runners. Make the entry shadow "
                  + "or remove it to keep this client's reviews on its runners")]
    private static partial void LogPrWideRunsInProcess(ILogger logger, Guid jobId, Guid clientId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Could not read the review pass list for client {ClientId}; its jobs stay reserved for runners this cycle")]
    private static partial void LogPassListUnreadable(ILogger logger, Guid clientId, Exception ex);
}

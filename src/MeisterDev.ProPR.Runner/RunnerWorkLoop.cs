// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Collections.Concurrent;
using MeisterDev.ProPR.Runner.Contracts;
using MeisterDev.ProPR.Runner.Execution;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Runner;

/// <summary>
///     The runner's whole job: ask for work when there is room, run it, hand the lease back.
///     <para>
///         The loop asks only when it has a free slot. That is what lets the control plane dispatch without
///         tracking runner capacity itself, and it means a runner that stops responding removes its own
///         capacity from the pool by not asking again.
///     </para>
///     <para>
///         Nothing here exits on a bad answer. A quiet queue, a full slot pool, an unreachable control
///         plane and a rejected contract are all conditions a host should report and keep running through:
///         a container that crash-loops tells an operator far less than one that stays up saying why it is
///         idle, and an orchestrator restarting it changes nothing about the cause.
///     </para>
/// </summary>
public sealed partial class RunnerWorkLoop(
    ControlPlaneClient controlPlane,
    RunnerCredentialStore credentials,
    IRunnerJobExecutor executor,
    Execution.WorkspaceFetcher workspaces,
    IOptions<RunnerHostOptions> options,
    RunnerHealthState health,
    TimeProvider timeProvider,
    ILogger<RunnerWorkLoop> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, LeasedJob> _inFlight = new();

    /// <summary>How many jobs this runner is working on right now.</summary>
    public int InFlightCount => this._inFlight.Count;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogRunnerStarted(logger, options.Value.DisplayName, options.Value.Capacity);

        // Before asking for anything. A runner that died mid-job left a working copy of a customer's source
        // on disk, and the first thing a restarted host should do is get rid of it rather than add to it.
        workspaces.Purge();

        var consecutiveFailures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = options.Value.PollInterval;

            try
            {
                // Enrollment before anything else, because every other call presents the credential it
                // produces. A host that cannot enrol keeps trying rather than exiting: an operator has to
                // see why, and a restart loop hides it.
                //
                // Deliberately not an early `continue`. The wait at the end of this loop is the only thing
                // pacing it, and skipping it turned a host that could not enrol into roughly nine hundred
                // requests a second against the endpoint that had just rate-limited it.
                if (await this.EnsureCredentialAsync(stoppingToken))
                {
                    var freeSlots = options.Value.Capacity - this._inFlight.Count;

                    // A full runner does not ask. Asking anyway would make the control plane's answer depend
                    // on capacity it cannot see, which is the coordination this design exists to avoid.
                    if (freeSlots > 0)
                    {
                        var result = await controlPlane.RequestLeaseAsync(freeSlots, stoppingToken);
                        consecutiveFailures = this.Apply(result, ref delay, consecutiveFailures);
                    }
                }
                else
                {
                    consecutiveFailures++;
                    delay = this.Backoff(consecutiveFailures);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // A loop that dies on an unexpected exception is a host an operator has to restart to diagnose.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogLoopIterationFailed(logger, ex);
                consecutiveFailures++;
                delay = this.Backoff(consecutiveFailures);
            }

            try
            {
                await Task.Delay(delay, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await this.DrainAsync();
    }

    /// <summary>
    ///     Acts on one lease answer and returns the new consecutive-failure count. Only the answers that
    ///     mean "the control plane is not currently usable" back off; a quiet queue keeps polling at its
    ///     ordinary interval, because backing off on an empty queue would make a runner slow to pick up the
    ///     first job after a lull.
    /// </summary>
    private int Apply(LeaseResult result, ref TimeSpan delay, int consecutiveFailures)
    {
        switch (result.Outcome)
        {
            case LeaseOutcome.Leased:
                // The same https-or-loopback rule the host applies to its configured URL, applied to the
                // address the manifest tells this job to call. Falling back to the configured URL instead
                // would reintroduce the split-brain the address exists to prevent, so the job is
                // handed back and the misconfiguration is reported where an operator reads it.
                if (!RunnerReplicaAffinity.TryValidate(result.Manifest!.ServedBy, out var affinityError))
                {
                    LogServedByRejected(logger, result.Manifest.JobId, affinityError!);
                    _ = controlPlane.ReleaseLeaseAsync(result.Manifest.JobId, result.Manifest.LeaseGeneration, CancellationToken.None);
                    health.Report(RunnerHealthState.Status.Refused, affinityError);
                    delay = this.Backoff(consecutiveFailures + 1);
                    return consecutiveFailures + 1;
                }

                this.Start(result.Manifest!);
                health.Report(RunnerHealthState.Status.Working, null);

                // Immediately ask again rather than sleeping: a runner with several free slots should fill
                // them in one burst instead of one job per poll interval.
                delay = TimeSpan.Zero;
                return 0;

            case LeaseOutcome.NoWork:
                health.Report(RunnerHealthState.Status.Idle, null);
                return 0;

            case LeaseOutcome.NoSlot:
                LogNoSlot(logger, result.Detail ?? "no detail");
                health.Report(RunnerHealthState.Status.Idle, result.Detail);
                return 0;

            case LeaseOutcome.Draining:
                // Backed off rather than polled at the normal rate: a drain lasts as long as an upgrade,
                // and repeatedly calling a control plane that is shedding work has no benefit. Reported
                // as draining rather than idle so an operator sees the upgrade rather than a capacity fault.
                LogControlPlaneDraining(logger, result.Detail ?? "no detail");
                health.Report(RunnerHealthState.Status.Draining, result.Detail);
                delay = this.Backoff(consecutiveFailures + 1);
                return consecutiveFailures + 1;

            case LeaseOutcome.ContractRejected:
                // Terminal for leasing but not for the process. An operator needs to see the version skew;
                // exiting would hide it behind a restart loop.
                LogContractRejected(logger, RunnerContractVersion.Current, result.Detail ?? "no detail");
                health.Report(RunnerHealthState.Status.Refused, result.Detail);
                delay = this.Backoff(consecutiveFailures + 1);
                return consecutiveFailures + 1;

            case LeaseOutcome.RegistrationRejected:
                LogRegistrationRejected(logger, result.Detail ?? "no detail");
                health.Report(RunnerHealthState.Status.Refused, result.Detail);
                delay = this.Backoff(consecutiveFailures + 1);
                return consecutiveFailures + 1;

            default:
                health.Report(RunnerHealthState.Status.Disconnected, result.Detail);
                delay = this.Backoff(consecutiveFailures + 1);
                return consecutiveFailures + 1;
        }
    }

    /// <summary>
    ///     Makes sure this host holds a usable credential: enrolling when it has none, and renewing before
    ///     the one it holds expires.
    ///     <para>
    ///         Renewed early rather than at expiry. Renewing at expiry leaves a window where every call
    ///         fails while the renewal races them, and one of those calls is the heartbeat keeping a review
    ///         alive.
    ///     </para>
    /// </summary>
    /// <returns>Whether the host may go on to ask for work.</returns>
    private async Task<bool> EnsureCredentialAsync(CancellationToken ct)
    {
        if (!credentials.IsEnrolled)
        {
            var token = options.Value.RegistrationToken;
            if (string.IsNullOrWhiteSpace(token))
            {
                // Neither a credential nor a way to get one. Said once per cycle rather than at startup
                // only, because an operator who fixes the configuration restarts the container anyway and
                // an operator who has not yet should keep seeing it.
                LogNotEnrollable(logger);
                health.Report(RunnerHealthState.Status.Refused, "This host has neither a credential nor a registration token.");
                return false;
            }

            var enrolled = await controlPlane.EnrollAsync(token, options.Value.DisplayName, options.Value.Tags, ct);
            if (!enrolled.Succeeded)
            {
                LogEnrollmentRefused(logger, enrolled.Refusal ?? "no detail");
                health.Report(RunnerHealthState.Status.Refused, enrolled.Refusal);
                return false;
            }

            credentials.Set(enrolled.Credential!, enrolled.ExpiresAt);
            LogEnrolled(logger, options.Value.DisplayName, enrolled.ExpiresAt);
            return true;
        }

        if (!credentials.NeedsRenewal(timeProvider.GetUtcNow()))
        {
            return true;
        }

        var renewed = await controlPlane.RenewCredentialAsync(ct);
        if (renewed.Succeeded)
        {
            credentials.Set(renewed.Credential!, renewed.ExpiresAt);
            LogCredentialRenewed(logger, renewed.ExpiresAt);
            return true;
        }

        // Carries on with the credential it has. It is still valid until it expires, and refusing to work
        // for the last hour of a credential's life because the renewal failed once has no benefit.
        LogRenewalFailed(logger, renewed.Refusal ?? "no detail");
        return true;
    }

    private void Start(RunnerJobManifest manifest)
    {
        // Deliberately not linked to the stopping token. Linking would have shutdown cancel every job the
        // instant it began, so the jobs would tear themselves down and leave the drain nothing to
        // enumerate, and their leases would be left to expire rather than returned. The drain owns how
        // shutdown reaches a job, which keeps that decision in one readable place.
        var jobCts = new CancellationTokenSource();
        var leased = new LeasedJob(manifest, jobCts);

        // Registered before the work starts, so a drain that begins in the same instant still finds it.
        this._inFlight[manifest.JobId] = leased;

        // Renewal runs beside the work, not inside it. A single model call can outlast a whole lease, so
        // a heartbeat driven by pipeline progress would let a healthy review lose its job. It is also the
        // only channel that reaches a job already in flight: a stop, a supersede, and an exhausted budget
        // all arrive here as a refused renewal.
        var heartbeat = Task.Run(() => this.HeartbeatUntilDoneAsync(manifest, jobCts), CancellationToken.None);

        leased.Work = Task.Run(
            async () =>
            {
                try
                {
                    await executor.ExecuteAsync(manifest, jobCts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Cancelled because the host is draining or the lease was lost. Neither is a failure of
                    // this job, and neither releases the lease here: the drain does that for everything it
                    // holds, and releasing again would hand back a lease another runner may already hold.
                }
#pragma warning disable CA1031 // One job failing must not take the loop with it.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    LogJobFailed(logger, manifest.JobId, ex);

                    // Named as the failure it is. A drain-shaped release here cost the job nothing, so a
                    // host that failed every attempt re-leased its own failure forever at full relay cost.
                    await controlPlane.ReleaseLeaseAsync(
                        manifest.JobId,
                        manifest.LeaseGeneration,
                        CancellationToken.None,
                        manifest.ServedBy,
                        RunnerLeaseReleaseReasons.Failure);
                }
                finally
                {
                    // Purged however the job ended. Job-scoped content outliving the job is the whole
                    // reason a runner host is treated as untrusted storage in the first place.
                    workspaces.Purge(manifest.JobId);
                    this._inFlight.TryRemove(manifest.JobId, out _);

                    // Stopped before the token source it reads is disposed, so a renewal in flight cannot
                    // observe a disposed source and take the loop down with it.
                    await jobCts.CancelAsync();
                    await heartbeat;
                    jobCts.Dispose();
                }
            },
            CancellationToken.None);
    }

    /// <summary>
    ///     Renews one job's lease until the job ends, and cancels it when the lease is gone.
    ///     <para>
    ///         The interval follows the expiry the control plane reports rather than a local constant, so a
    ///         runner and a control plane configured differently still agree on how often is often enough.
    ///     </para>
    ///     <para>
    ///         An unreachable control plane is ridden out rather than acted on. It has not said the lease is
    ///         gone, and abandoning a review on a transient fault would throw away everything the review has
    ///         already spent. The lease expires on its own if the outage lasts.
    ///     </para>
    /// </summary>
    private async Task HeartbeatUntilDoneAsync(RunnerJobManifest manifest, CancellationTokenSource jobCts)
    {
        var interval = TimeSpan.FromSeconds(20);

        while (!jobCts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, timeProvider, jobCts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var beat = await controlPlane.HeartbeatAsync(manifest.JobId, manifest.LeaseGeneration, CancellationToken.None, manifest.ServedBy);
            if (beat.Unreachable)
            {
                continue;
            }

            if (!beat.Held)
            {
                LogLeaseLost(logger, manifest.JobId, manifest.LeaseGeneration, beat.StopReason);
                await jobCts.CancelAsync();
                return;
            }

            if (beat.ExpiresAt is { } expiresAt)
            {
                // A third of the remaining life, so two renewals can be lost before the lease is.
                var remaining = expiresAt - timeProvider.GetUtcNow();
                interval = remaining > TimeSpan.Zero
                    ? TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(5).Ticks, remaining.Ticks / 3))
                    : TimeSpan.FromSeconds(5);
            }
        }
    }

    /// <summary>
    ///     Exponential backoff, capped. Capped rather than unbounded so a control plane that comes back
    ///     after an hour is noticed in the next minute rather than the next hour.
    /// </summary>
    private TimeSpan Backoff(int consecutiveFailures)
    {
        var seconds = Math.Min(
            options.Value.MaxBackoffSeconds,
            options.Value.PollIntervalSeconds * Math.Pow(2, Math.Min(consecutiveFailures, 10)));
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    ///     Stops taking work and hands back what is held, so a planned restart costs its jobs nothing.
    ///     <para>
    ///         Released rather than waited out: a lease left to expire keeps its job unavailable for the
    ///         whole lease duration and spends one of the job's reclaim attempts on an event that was not a
    ///         failure at all.
    ///     </para>
    /// </summary>
    private async Task DrainAsync()
    {
        var held = this._inFlight.Values.ToArray();
        if (held.Length == 0)
        {
            LogRunnerStopped(logger, 0);
            return;
        }

        LogDraining(logger, held.Length);

        foreach (var job in held)
        {
            try
            {
                await job.Cancellation.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // The job finished between the snapshot and here and disposed its own source. Nothing to
                // cancel, and nothing to report: it is the ordinary race on a host that is shutting down.
            }
        }

        // Every held lease is returned, and one job failing to give its back must not cost the others
        // theirs. An earlier version let a single exception here abort the loop, which meant a job that
        // happened to finish mid-shutdown left every remaining lease held until it expired.
        foreach (var job in held)
        {
            try
            {
                await controlPlane.ReleaseLeaseAsync(
                    job.Manifest.JobId,
                    job.Manifest.LeaseGeneration,
                    CancellationToken.None,
                    job.Manifest.ServedBy);
            }
#pragma warning disable CA1031 // A drain that throws leaves leases held; finishing the sweep matters more.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogDrainReleaseFailed(logger, job.Manifest.JobId, ex);
            }
        }

        LogRunnerStopped(logger, held.Length);
    }

    private sealed record LeasedJob(RunnerJobManifest Manifest, CancellationTokenSource Cancellation)
    {
        public Task? Work { get; set; }
    }

    [LoggerMessage(
        EventId = 6012,
        Level = LogLevel.Error,
        Message =
            "This host has neither a runner credential nor a registration token, so it cannot enrol. Set RUNNER_REGISTRATION_TOKEN to a token issued in the admin UI.")]
    private static partial void LogNotEnrollable(ILogger logger);

    [LoggerMessage(EventId = 6013, Level = LogLevel.Error, Message = "Enrollment was refused: {Reason}")]
    private static partial void LogEnrollmentRefused(ILogger logger, string reason);

    [LoggerMessage(EventId = 6014, Level = LogLevel.Information, Message = "Enrolled as {DisplayName}; the credential expires {ExpiresAt}")]
    private static partial void LogEnrolled(ILogger logger, string displayName, DateTimeOffset? expiresAt);

    [LoggerMessage(EventId = 6015, Level = LogLevel.Information, Message = "Renewed this runner's credential; it now expires {ExpiresAt}")]
    private static partial void LogCredentialRenewed(ILogger logger, DateTimeOffset? expiresAt);

    [LoggerMessage(
        EventId = 6016,
        Level = LogLevel.Warning,
        Message = "Renewing this runner's credential failed and will be retried; the current one is still valid until it expires: {Reason}")]
    private static partial void LogRenewalFailed(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 6011,
        Level = LogLevel.Warning,
        Message = "Job {JobId} generation {Generation} is no longer this runner's to execute ({StopReason}); it has been stopped")]
    private static partial void LogLeaseLost(ILogger logger, Guid jobId, int generation, string stopReason);

    [LoggerMessage(EventId = 6001, Level = LogLevel.Information, Message = "Runner {DisplayName} started with capacity {Capacity}")]
    private static partial void LogRunnerStarted(ILogger logger, string displayName, int capacity);

    [LoggerMessage(EventId = 6002, Level = LogLevel.Information, Message = "Runner stopped, having released {ReleasedCount} lease(s)")]
    private static partial void LogRunnerStopped(ILogger logger, int releasedCount);

    [LoggerMessage(EventId = 6003, Level = LogLevel.Information, Message = "Draining {HeldCount} lease(s) before shutdown")]
    private static partial void LogDraining(ILogger logger, int heldCount);

    [LoggerMessage(EventId = 6004, Level = LogLevel.Warning, Message = "Review job {JobId} failed on this runner and its lease was returned")]
    private static partial void LogJobFailed(ILogger logger, Guid jobId, Exception ex);

    [LoggerMessage(EventId = 6005, Level = LogLevel.Warning, Message = "The runner loop hit an unexpected error and will retry")]
    private static partial void LogLoopIterationFailed(ILogger logger, Exception ex);

    [LoggerMessage(
        EventId = 6006,
        Level = LogLevel.Error,
        Message =
            "This control plane cannot serve contract version {ContractVersion}, so no work will be leased "
            + "until one of the two is upgraded: {Detail}")]
    private static partial void LogContractRejected(ILogger logger, int contractVersion, string detail);

    [LoggerMessage(EventId = 6007, Level = LogLevel.Error, Message = "This runner's registration was rejected: {Detail}")]
    private static partial void LogRegistrationRejected(ILogger logger, string detail);

    [LoggerMessage(EventId = 6008, Level = LogLevel.Information, Message = "No runner slot is free: {Detail}")]
    private static partial void LogNoSlot(ILogger logger, string detail);

    [LoggerMessage(
        EventId = 6009, Level = LogLevel.Warning, Message = "Draining could not return the lease on job {JobId}; it will be reclaimed on expiry instead")]
    private static partial void LogDrainReleaseFailed(ILogger logger, Guid jobId, Exception ex);

    [LoggerMessage(
        EventId = 6010,
        Level = LogLevel.Information,
        Message = "The control plane is draining and is issuing no new leases: {Detail}")]
    private static partial void LogControlPlaneDraining(ILogger logger, string detail);

    [LoggerMessage(
        EventId = 6011,
        Level = LogLevel.Error,
        Message = "The lease on job {JobId} was handed back: {Reason}. Fix RUNNER_ADVERTISED_URL on the control-plane replica.")]
    private static partial void LogServedByRejected(ILogger logger, Guid jobId, string reason);
}

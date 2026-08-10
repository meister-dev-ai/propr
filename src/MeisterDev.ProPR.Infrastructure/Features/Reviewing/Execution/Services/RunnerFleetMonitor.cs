// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Runner.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Services;

/// <summary>
///     The one place that decides whether this installation is currently running reviews on runners.
///     <para>
///         A runner counts as active when it is enrolled, not revoked, speaks a contract this control plane
///         can serve, and has been heard from inside the configured window. All four matter: a revoked
///         runner that is still heartbeating would otherwise keep the control plane from executing anything,
///         and so would one whose contract version this build cannot serve, which is the version-skew case
///         that turns a rolling upgrade into an outage.
///     </para>
/// </summary>
public sealed class RunnerFleetMonitor(
    MeisterProPRDbContext dbContext,
    IRunnerLeaseOfferStore offers,
    IOptions<RunnerFleetOptions> options,
    TimeProvider timeProvider) : IRunnerFleetMonitor
{
    // Static because the hysteresis has to outlive the scoped instances that observe it. Per-replica by
    // design: each control plane decides whether it may execute, and a replica that has not yet seen the
    // fleet empty for long enough simply keeps waiting, which is the safe direction.
    private static DateTimeOffset? _fleetEmptySince;
    private static readonly Lock Gate = new();

    /// <inheritdoc />
    public async Task<RunnerFleetStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow();
        var activeSince = now - options.Value.ActiveHeartbeatWindow;

        var snapshot = await offers.GetFleetSnapshotAsync(
            activeSince,
            RunnerContractVersion.Oldest,
            RunnerContractVersion.Current,
            ct);

        var mode = this.ResolveMode(snapshot.ActiveRunnerCount, snapshot.RegisteredRunnerCount, now);
        var stall = await this.DetectStallAsync(
            mode,
            snapshot.ActiveRunnerCount,
            snapshot.RegisteredRunnerCount,
            activeSince,
            now,
            ct);

        return new RunnerFleetStatus(mode, snapshot.ActiveRunnerCount, stall, snapshot.ClientsWithActiveRunner);
    }

    /// <summary>
    ///     Where reviews execute, with hysteresis on the way back to in-process only.
    ///     <para>
    ///         Becoming distributed is immediate: the moment a runner is active, this control plane stops
    ///         executing. Going back is delayed by the settle period, so a runner flapping around the
    ///         heartbeat window cannot toggle the execution mode on every poll. The asymmetry is deliberate,
    ///         because the two mistakes are not equal: waiting too long to resume in-process execution
    ///         delays work, while resuming it too eagerly breaks the isolation the installation was promised.
    ///     </para>
    /// </summary>
    private ReviewExecutionMode ResolveMode(int activeCount, int registeredCount, DateTimeOffset now)
    {
        // An installation with no runners at all is not a distributed installation and never enters the
        // hysteresis at all. This is the path every existing deployment stays on.
        if (registeredCount == 0)
        {
            lock (Gate)
            {
                _fleetEmptySince = null;
            }

            return ReviewExecutionMode.InProcess;
        }

        if (activeCount > 0)
        {
            lock (Gate)
            {
                _fleetEmptySince = null;
            }

            return ReviewExecutionMode.RunnersOnly;
        }

        lock (Gate)
        {
            _fleetEmptySince ??= now;
            var emptyFor = now - _fleetEmptySince.Value;
            return emptyFor >= options.Value.FleetEmptySettle
                ? ReviewExecutionMode.InProcess
                : ReviewExecutionMode.RunnersOnly;
        }
    }

    private async Task<QueueStallCondition?> DetectStallAsync(
        ReviewExecutionMode mode,
        int activeCount,
        int registeredCount,
        DateTimeOffset activeSince,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // A control plane that is executing its own work cannot be stalled for want of a runner. This is
        // the ordinary single-process installation and it must not start reporting stalls.
        if (mode == ReviewExecutionMode.InProcess)
        {
            return null;
        }

        var oldest = await dbContext.ReviewJobs
            .AsNoTracking()
            .Where(j => j.Status == JobStatus.Pending)
            .OrderBy(j => j.SubmittedAt)
            .Select(j => (DateTimeOffset?)j.SubmittedAt)
            .FirstOrDefaultAsync(ct);

        if (oldest is null || now - oldest.Value < options.Value.QueueStallGrace)
        {
            return null;
        }

        var pendingCount = await dbContext.ReviewJobs
            .AsNoTracking()
            .CountAsync(j => j.Status == JobStatus.Pending, ct);

        // Tag mismatch is reported ahead of the fleet being offline, because it is the cause that stays
        // true after the fleet comes back: an operator who only sees "no active runner" restarts runners
        // and watches the same jobs sit there.
        var unroutable = await offers.GetUnroutableJobsAsync(activeSince, 5, ct);
        if (unroutable.Count > 0)
        {
            var tags = string.Join(", ", unroutable.SelectMany(j => j.RequiredTags).Distinct().Order());
            return new QueueStallCondition(
                QueueStallCause.NoRunnerMatchesRequiredTags,
                pendingCount,
                oldest.Value,
                $"No active runner declares the tags these jobs require: {tags}.");
        }

        return activeCount == 0
            ? new QueueStallCondition(
                QueueStallCause.NoActiveRunner,
                pendingCount,
                oldest.Value,
                $"{registeredCount} runner(s) are registered and none has been heard from recently.")
            : new QueueStallCondition(
                QueueStallCause.NoFreeSlot,
                pendingCount,
                oldest.Value,
                $"{activeCount} runner(s) are active but none is taking work.");
    }

    /// <summary>Forgets the hysteresis state. For tests, which must not inherit another test's fleet history.</summary>
    internal static void ResetHysteresis()
    {
        lock (Gate)
        {
            _fleetEmptySince = null;
        }
    }
}

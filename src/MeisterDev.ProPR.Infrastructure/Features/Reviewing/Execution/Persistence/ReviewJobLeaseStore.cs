// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;

/// <summary>
///     Claiming and liveness against PostgreSQL. Every operation here is a single conditional statement whose
///     predicate carries what the caller claims to be true, so the database decides the winner. Nothing is
///     resolved by reading a row, checking it in memory, and writing it back: two hosts doing that would both
///     be told they won.
/// </summary>
public sealed class ReviewJobLeaseStore(
    MeisterProPRDbContext dbContext,
    IJobRepository jobs,
    IOptions<ReviewLeaseOptions> leaseOptions,
    ILogger<ReviewJobLeaseStore> logger) : IReviewJobLeaseStore
{
    /// <summary>
    ///     How long the protocol cleanup after a duration-ceiling failure may take before it is abandoned.
    /// </summary>
    /// <remarks>
    ///     The renewal that carries the stop directive does not return until this call does, and the
    ///     execution keeps working until it receives that directive. Short enough to stay well inside a
    ///     heartbeat interval, so a slow database delays the stop by a fraction of one renewal at most.
    /// </remarks>
    private static readonly TimeSpan ProtocolCleanupTimeout = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public Task<IReadOnlyList<ReviewJob>> GetClaimCandidatesAsync(
        int limit,
        DateTimeOffset? submittedAfter = null,
        CancellationToken ct = default)
    {
        return jobs.GetClaimCandidatesAsync(limit, submittedAfter, ct);
    }

    /// <inheritdoc />
    public async Task<ReviewJobLease?> TryClaimAsync(
        Guid jobId,
        string owner,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);

        // One statement does all of it: the status move, the owner stamp, the generation bump, and the
        // expiry. The inner SELECT re-checks eligibility under a row lock and SKIP LOCKED turns a
        // concurrent claimant into a clean miss instead of a wait. Expiry comes from the database clock so
        // hosts with skewed clocks still agree on when a lease ends.
        var claimed = await dbContext.Database.ExecuteSqlRawAsync(
            """
            UPDATE review_jobs
            SET status = 'Processing',
                lease_owner = {1},
                lease_generation = lease_generation + 1,
                lease_expires_at = now() + make_interval(secs => {2}),
                last_heartbeat_at = now(),
                processing_started_at = now(),
                publishing_started_at = NULL
            WHERE id = (
                SELECT id FROM review_jobs
                WHERE id = {0} AND status = 'Pending'
                FOR UPDATE SKIP LOCKED
            )
            """,
            [jobId, owner, leaseDuration.TotalSeconds],
            ct);

        if (claimed == 0)
        {
            return null;
        }

        // Read back what the claim just stamped. Only an expiry-driven reclaim could change these, and a
        // lease granted microseconds ago cannot already have expired.
        var granted = await dbContext.ReviewJobs
            .AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => new { j.LeaseGeneration, j.LeaseExpiresAt })
            .SingleOrDefaultAsync(ct);

        return granted?.LeaseExpiresAt is null
            ? null
            : new ReviewJobLease(jobId, owner, granted.LeaseGeneration, granted.LeaseExpiresAt.Value);
    }

    /// <inheritdoc />
    public async Task<ReviewJobLeaseRenewal> TryRenewAsync(
        ReviewJobLease lease,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);

        // The predicate carries the guarantee: owner and generation must both still match, and the job must
        // still be executing. A holder that was reclaimed carries an older generation and renews nothing.
        // The last clause applies the ceiling on one execution. An execution continues only while its
        // renewals succeed, so refusing the renewal stops one that has run past the ceiling.
        //
        // A row without a start timestamp is renewed and stamped with one. The claim records the timestamp,
        // so this covers a row that reached Processing another way: accepting it unstamped would leave that
        // execution outside the ceiling for as long as it kept renewing, and refusing it would fail a job
        // over a missing timestamp. Stamping it starts the allowance from this renewal.
        var renewed = await dbContext.Database.ExecuteSqlRawAsync(
            """
            UPDATE review_jobs
            SET lease_expires_at = now() + make_interval(secs => {3}),
                last_heartbeat_at = now(),
                processing_started_at = COALESCE(processing_started_at, now())
            WHERE id = {0} AND lease_owner = {1} AND lease_generation = {2} AND status = 'Processing'
              AND (processing_started_at IS NULL
                   OR processing_started_at > now() - make_interval(secs => {4}))
            """,
            [
                lease.JobId,
                lease.Owner,
                lease.Generation,
                leaseDuration.TotalSeconds,
                leaseOptions.Value.MaxReviewDuration.TotalSeconds,
            ],
            ct);

        if (renewed == 0)
        {
            // A refusal has two very different causes, and the holder needs to know which. Either the job
            // was halted by a decision someone made about it, which it should report as that outcome, or it
            // simply no longer owns the job, in which case whoever does owns the outcome too.
            return await this.ExplainRefusedRenewalAsync(lease, ct).ConfigureAwait(false);
        }

        var expiresAt = await dbContext.ReviewJobs
            .AsNoTracking()
            .Where(j => j.Id == lease.JobId)
            .Select(j => j.LeaseExpiresAt)
            .SingleOrDefaultAsync(ct);

        return new ReviewJobLeaseRenewal(true, expiresAt);
    }

    /// <summary>
    ///     Works out why a renewal was refused. The persisted job status is the cross-host source of truth
    ///     for a stop, a supersede, and a budget cut, so reading it here is what carries those decisions to
    ///     an execution running somewhere the process that made them cannot reach.
    /// </summary>
    private async Task<ReviewJobLeaseRenewal> ExplainRefusedRenewalAsync(
        ReviewJobLease lease,
        CancellationToken ct)
    {
        var current = await dbContext.ReviewJobs
            .AsNoTracking()
            .Where(j => j.Id == lease.JobId)
            .Select(j => new { j.Status, j.LeaseGeneration, j.LeaseOwner, j.ProcessingStartedAt })
            .SingleOrDefaultAsync(ct);

        if (current is null)
        {
            return ReviewJobLeaseRenewal.Rejected;
        }

        // The generation, not the owner, is what identifies this caller here. Reaching a terminal state
        // clears the owner precisely so nothing looks leased afterwards, and the generation is deliberately
        // left in place so a caller can still be recognised as the one that held it.
        if (current.LeaseGeneration != lease.Generation)
        {
            return ReviewJobLeaseRenewal.Rejected;
        }

        if (current.Status == JobStatus.Processing
            && !string.Equals(current.LeaseOwner, lease.Owner, StringComparison.Ordinal))
        {
            return ReviewJobLeaseRenewal.Rejected;
        }

        // The job is still executing, still owned by this caller, still on this generation, and the renewal
        // was refused. Every predicate of the renewal statement has been re-checked above except the duration
        // ceiling, so that is the clause that refused it. Deducing it leaves the database's clock as the only
        // clock involved; comparing the elapsed time here would introduce this process's clock as a second
        // one. A predicate added to that statement has to be re-checked above, or it will be reported as a
        // duration breach.
        if (current.Status == JobStatus.Processing && current.ProcessingStartedAt is not null)
        {
            return await this.FailForMaxDurationAsync(lease, ct);
        }

        return current.Status switch
        {
            JobStatus.Stopped => ReviewJobLeaseRenewal.StoppedBecause(ReviewJobStopReason.OperatorStop),
            JobStatus.Superseded => ReviewJobLeaseRenewal.StoppedBecause(ReviewJobStopReason.Superseded),
            JobStatus.BudgetExceeded => ReviewJobLeaseRenewal.StoppedBecause(ReviewJobStopReason.BudgetCapReached),
            JobStatus.Cancelled => ReviewJobLeaseRenewal.StoppedBecause(ReviewJobStopReason.OperatorStop),
            _ => ReviewJobLeaseRenewal.Rejected,
        };
    }

    /// <summary>
    ///     Fails the job for passing the ceiling on one execution, and only while this caller still holds it.
    /// </summary>
    /// <remarks>
    ///     The read that led here is not part of the statement that refused the renewal, so between the two
    ///     this process can be paused long enough for the lease to expire, another host to reclaim the job
    ///     and start a new generation, and this call to arrive afterwards. An unfenced terminal write would
    ///     then fail that host's running execution and clear its lease, so the transition carries the owner
    ///     and generation it believes it holds and is refused if either has moved on.
    ///     <para>
    ///         The statement carries the whole terminal state: the status, the classified reason, the message,
    ///         the completion stamp and the cleared lease. The classification is what tells an operator a
    ///         failed job was stopped for its duration rather than interrupted, which the other terminal
    ///         paths in this class record for the same purpose. Writing the status first and the rest afterwards would leave a job
    ///         that is terminal but still stamped with an owner, and with no reason recorded, if the second
    ///         write did not happen — which host shutdown, cancelling the token between the two, would cause.
    ///         The protocol cleanup that follows is not part of that state and runs uncancellable, so a
    ///         shutdown cannot stop it either.
    ///     </para>
    /// </remarks>
    private async Task<ReviewJobLeaseRenewal> FailForMaxDurationAsync(ReviewJobLease lease, CancellationToken ct)
    {
        var reason = $"The review ran longer than the {leaseOptions.Value.MaxReviewDurationMinutes}-minute "
                     + "ceiling on one execution and was stopped.";

        var fenced = await dbContext.Database.ExecuteSqlRawAsync(
            """
            UPDATE review_jobs
            SET status = 'Failed',
                failure_reason = {4},
                error_message = {3},
                completed_at = now(),
                lease_owner = NULL,
                lease_expires_at = NULL,
                last_heartbeat_at = NULL,
                publishing_started_at = NULL
            WHERE id = {0} AND lease_owner = {1} AND lease_generation = {2} AND status = 'Processing'
            """,
            [lease.JobId, lease.Owner, lease.Generation, reason, (int)ReviewJobFailureReason.MaxDurationExceeded],
            ct);

        if (fenced == 0)
        {
            return ReviewJobLeaseRenewal.Rejected;
        }

        // The row is terminal and unclaimable now, so this closes the protocols the execution left open
        // against a job nobody else can be holding. CancellationToken.None: the durable state is already
        // written, and a shutdown here would leave open protocol rows behind a completed transition.
        //
        // Failures are recorded and not propagated, and the wait is bounded. Throwing would reach the
        // heartbeat as a transient renewal error, which retries rather than stopping; blocking would hold the
        // heartbeat in this call. Either way the execution would keep running against a job the database
        // already shows as failed, and could still reach publication. Open protocol rows are the smaller
        // loss, and the durable state this stop rests on is already written.
        using var cleanupTimeout = new CancellationTokenSource(ProtocolCleanupTimeout);
        try
        {
            await jobs.SetFailedAsync(lease.JobId, reason, cleanupTimeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Review job {JobId} was failed for exceeding its duration ceiling, but the protocol cleanup that follows did not complete.",
                lease.JobId);
        }

        return ReviewJobLeaseRenewal.StoppedBecause(ReviewJobStopReason.MaxDurationExceeded);
    }

    /// <inheritdoc />
    public async Task<bool> TryReleaseAsync(ReviewJobLease lease, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);

        // A deliberate hand-back: the job returns to the claimable pool with its lease cleared. The
        // generation is left where it is, so the releasing party cannot come back and pass a fencing check.
        var released = await dbContext.Database.ExecuteSqlRawAsync(
            """
            UPDATE review_jobs
            SET status = 'Pending',
                lease_owner = NULL,
                lease_expires_at = NULL,
                last_heartbeat_at = NULL,
                publishing_started_at = NULL
            WHERE id = {0} AND lease_owner = {1} AND lease_generation = {2} AND status = 'Processing'
            """,
            [lease.JobId, lease.Owner, lease.Generation],
            ct);

        return released > 0;
    }

    /// <inheritdoc />
    public async Task ClearLeaseAsync(Guid jobId, CancellationToken ct = default)
    {
        await dbContext.ReviewJobs
            .Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(j => j.LeaseOwner, (string?)null)
                    .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(j => j.LastHeartbeatAt, (DateTimeOffset?)null),
                ct);
    }

    /// <inheritdoc />
    public async Task<bool> IsLeaseCurrentAsync(ReviewJobLease lease, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);

        return await dbContext.ReviewJobs
            .AsNoTracking()
            .AnyAsync(
                j => j.Id == lease.JobId
                     && j.LeaseOwner == lease.Owner
                     && j.LeaseGeneration == lease.Generation
                     && j.Status == JobStatus.Processing,
                ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExpiredReviewJobLease>> GetExpiredLeasesAsync(
        int limit,
        TimeSpan reclaimBackoff,
        TimeSpan publicationTimeout,
        CancellationToken ct = default)
    {
        if (limit < 1)
        {
            return [];
        }

        // Expiry is compared against the database clock on both sides, so a host whose own clock runs fast
        // cannot decide another host's lease has ended early.
        var now = await this.GetDatabaseTimeAsync(ct).ConfigureAwait(false);
        var backoffCutoff = now - reclaimBackoff;
        var publishingCutoff = now - publicationTimeout;

        var rows = await dbContext.ReviewJobs
            .AsNoTracking()
            .Where(j => j.Status == JobStatus.Processing
                        && j.LeaseExpiresAt != null
                        && j.LeaseExpiresAt < now
                        // Publication is protected by its own, longer timeout: reclaiming a job while its
                        // comments are going out is how a review gets posted twice.
                        && (j.PublishingStartedAt == null || j.PublishingStartedAt < publishingCutoff)
                        && (j.LastReclaimedAt == null || j.LastReclaimedAt < backoffCutoff))
            .OrderBy(j => j.LeaseExpiresAt)
            .Take(limit)
            .Select(j => new { j.Id, j.LeaseGeneration, j.LeaseExpiresAt })
            .ToListAsync(ct);

        return rows
            .Select(row => new ExpiredReviewJobLease(row.Id, row.LeaseGeneration, row.LeaseExpiresAt!.Value))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<ReviewJobReclaimOutcome> TryReclaimAsync(
        ExpiredReviewJobLease expired,
        int maxConsecutiveReclaims,
        int maxTotalReclaims,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expired);

        // One statement decides both the counting and the outcome. Splitting "is the budget spent?" from
        // "take the job back" would let two hosts read the same counts and both act on them, and the
        // generation predicate is what makes a holder that recovered and renewed win over this sweep.
        var reclaimed = await dbContext.Database.ExecuteSqlRawAsync(
            """
            UPDATE review_jobs
            SET consecutive_reclaim_count = consecutive_reclaim_count + 1,
                total_reclaim_count = total_reclaim_count + 1,
                last_reclaimed_at = now(),
                lease_owner = NULL,
                lease_expires_at = NULL,
                last_heartbeat_at = NULL,
                publishing_started_at = NULL,
                status = CASE
                    WHEN consecutive_reclaim_count + 1 > {2} OR total_reclaim_count + 1 > {3}
                    THEN 'Failed' ELSE 'Pending' END,
                failure_reason = CASE
                    WHEN consecutive_reclaim_count + 1 > {2} OR total_reclaim_count + 1 > {3}
                    THEN {4} ELSE failure_reason END,
                error_message = CASE
                    WHEN consecutive_reclaim_count + 1 > {2} OR total_reclaim_count + 1 > {3}
                    THEN {5} ELSE error_message END,
                completed_at = CASE
                    WHEN consecutive_reclaim_count + 1 > {2} OR total_reclaim_count + 1 > {3}
                    THEN now() ELSE completed_at END
            WHERE id = {0}
              AND lease_generation = {1}
              AND status = 'Processing'
              AND lease_expires_at IS NOT NULL
              AND lease_expires_at < now()
            """,
            [
                expired.JobId,
                expired.Generation,
                maxConsecutiveReclaims,
                maxTotalReclaims,
                (int)ReviewJobFailureReason.LeaseLost,
                "The review was interrupted more times than its reclaim budget allows without completing "
                + "further files. The last execution lost its lease and no attempt remains.",
            ],
            ct);

        if (reclaimed == 0)
        {
            return ReviewJobReclaimOutcome.NotReclaimed;
        }

        var status = await dbContext.ReviewJobs
            .AsNoTracking()
            .Where(j => j.Id == expired.JobId)
            .Select(j => j.Status)
            .SingleAsync(ct);

        return status == JobStatus.Failed
            ? ReviewJobReclaimOutcome.FailedOutOfReclaimBudget
            : ReviewJobReclaimOutcome.Requeued;
    }

    /// <inheritdoc />
    public async Task<ReviewJobReclaimOutcome> TryReleaseFailedAsync(
        ReviewJobLease lease,
        int maxConsecutiveReclaims,
        int maxTotalReclaims,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);

        // The reclaim statement's twin, predicated on the releasing party's own lease instead of on
        // expiry: the holder is handing the job back because its attempt failed, and that spends the same
        // budget an expiry would have. last_reclaimed_at is stamped so the failure also respects the
        // reclaim backoff wherever expiry does.
        var released = await dbContext.Database.ExecuteSqlRawAsync(
            """
            UPDATE review_jobs
            SET consecutive_reclaim_count = consecutive_reclaim_count + 1,
                total_reclaim_count = total_reclaim_count + 1,
                last_reclaimed_at = now(),
                lease_owner = NULL,
                lease_expires_at = NULL,
                last_heartbeat_at = NULL,
                publishing_started_at = NULL,
                status = CASE
                    WHEN consecutive_reclaim_count + 1 > {3} OR total_reclaim_count + 1 > {4}
                    THEN 'Failed' ELSE 'Pending' END,
                failure_reason = CASE
                    WHEN consecutive_reclaim_count + 1 > {3} OR total_reclaim_count + 1 > {4}
                    THEN {5} ELSE failure_reason END,
                error_message = CASE
                    WHEN consecutive_reclaim_count + 1 > {3} OR total_reclaim_count + 1 > {4}
                    THEN {6} ELSE error_message END,
                completed_at = CASE
                    WHEN consecutive_reclaim_count + 1 > {3} OR total_reclaim_count + 1 > {4}
                    THEN now() ELSE completed_at END
            WHERE id = {0} AND lease_owner = {1} AND lease_generation = {2} AND status = 'Processing'
            """,
            [
                lease.JobId,
                lease.Owner,
                lease.Generation,
                maxConsecutiveReclaims,
                maxTotalReclaims,
                (int)ReviewJobFailureReason.LeaseLost,
                "The review failed more times than its reclaim budget allows without completing further "
                + "files. The last execution handed its lease back after a failure and no attempt remains.",
            ],
            ct);

        if (released == 0)
        {
            return ReviewJobReclaimOutcome.NotReclaimed;
        }

        var status = await dbContext.ReviewJobs
            .AsNoTracking()
            .Where(j => j.Id == lease.JobId)
            .Select(j => j.Status)
            .SingleAsync(ct);

        return status == JobStatus.Failed
            ? ReviewJobReclaimOutcome.FailedOutOfReclaimBudget
            : ReviewJobReclaimOutcome.Requeued;
    }

    /// <inheritdoc />
    public async Task<bool> TryMarkPublishingAsync(
        Guid jobId,
        ReviewJobLease? lease = null,
        CancellationToken ct = default)
    {
        var marked = lease is null
            ? await dbContext.Database.ExecuteSqlRawAsync(
                """
                UPDATE review_jobs
                SET publishing_started_at = now()
                WHERE id = {0} AND status = 'Processing'
                """,
                [jobId],
                ct)
            : await dbContext.Database.ExecuteSqlRawAsync(
                """
                UPDATE review_jobs
                SET publishing_started_at = now()
                WHERE id = {0} AND lease_owner = {1} AND lease_generation = {2} AND status = 'Processing'
                """,
                [jobId, lease.Owner, lease.Generation],
                ct);

        return marked > 0;
    }

    /// <inheritdoc />
    public async Task ClearPublishingAsync(Guid jobId, CancellationToken ct = default)
    {
        await dbContext.ReviewJobs
            .Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.PublishingStartedAt, (DateTimeOffset?)null), ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> FailTimedOutPublicationsAsync(
        int limit,
        TimeSpan publicationTimeout,
        CancellationToken ct = default)
    {
        if (limit < 1)
        {
            return [];
        }

        var now = await this.GetDatabaseTimeAsync(ct).ConfigureAwait(false);
        var cutoff = now - publicationTimeout;

        var stuck = await dbContext.ReviewJobs
            .AsNoTracking()
            .Where(j => j.Status == JobStatus.Processing
                        && j.PublishingStartedAt != null
                        && j.PublishingStartedAt < cutoff)
            .OrderBy(j => j.PublishingStartedAt)
            .Take(limit)
            .Select(j => j.Id)
            .ToListAsync(ct);

        var failed = new List<Guid>(stuck.Count);
        foreach (var jobId in stuck)
        {
            // Surfaced distinctly rather than reclaimed: publication may have posted some comments already,
            // so another attempt could duplicate them. This one needs a person to look at it.
            var affected = await dbContext.ReviewJobs
                .Where(j => j.Id == jobId
                            && j.Status == JobStatus.Processing
                            && j.PublishingStartedAt != null
                            && j.PublishingStartedAt < cutoff)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(j => j.Status, JobStatus.Failed)
                        .SetProperty(j => j.FailureReason, ReviewJobFailureReason.PublicationTimedOut)
                        .SetProperty(
                            j => j.ErrorMessage,
                            "Publication of this review began and did not finish within its timeout. Some "
                            + "comments may already have been posted, so it was not retried automatically.")
                        .SetProperty(j => j.CompletedAt, DateTimeOffset.UtcNow)
                        .SetProperty(j => j.LeaseOwner, (string?)null)
                        .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null)
                        .SetProperty(j => j.LastHeartbeatAt, (DateTimeOffset?)null),
                    ct);

            if (affected > 0)
            {
                failed.Add(jobId);
            }
        }

        return failed;
    }

    /// <summary>
    ///     Reads the database clock. Every expiry decision is made against it rather than against a host
    ///     clock, so skew between hosts cannot make one of them consider another's lease finished early.
    /// </summary>
    private async Task<DateTimeOffset> GetDatabaseTimeAsync(CancellationToken ct)
    {
        var now = await dbContext.Database
            .SqlQueryRaw<DateTimeOffset>("SELECT now() AS \"Value\"")
            .SingleAsync(ct);
        return now;
    }
}

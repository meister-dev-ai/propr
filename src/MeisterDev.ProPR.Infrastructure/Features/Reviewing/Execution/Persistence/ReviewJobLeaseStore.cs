// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;

/// <summary>
///     Claiming and liveness against PostgreSQL. Every operation here is a single conditional statement whose
///     predicate carries what the caller believes to be true, so the database decides the winner. Nothing is
///     resolved by reading a row, checking it in memory, and writing it back: two hosts doing that both
///     believe they won.
/// </summary>
public sealed class ReviewJobLeaseStore(MeisterProPRDbContext dbContext, IJobRepository jobs) : IReviewJobLeaseStore
{
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

        // The predicate is the whole point: owner and generation must both still match, and the job must
        // still be executing. A holder that was reclaimed carries an older generation and renews nothing.
        var renewed = await dbContext.Database.ExecuteSqlRawAsync(
            """
            UPDATE review_jobs
            SET lease_expires_at = now() + make_interval(secs => {3}),
                last_heartbeat_at = now()
            WHERE id = {0} AND lease_owner = {1} AND lease_generation = {2} AND status = 'Processing'
            """,
            [lease.JobId, lease.Owner, lease.Generation, leaseDuration.TotalSeconds],
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
            .Select(j => new { j.Status, j.LeaseGeneration, j.LeaseOwner })
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

        return current.Status switch
        {
            JobStatus.Stopped => ReviewJobLeaseRenewal.StoppedBecause(ReviewJobStopReason.OperatorStop),
            JobStatus.Superseded => ReviewJobLeaseRenewal.StoppedBecause(ReviewJobStopReason.Superseded),
            JobStatus.BudgetExceeded => ReviewJobLeaseRenewal.StoppedBecause(ReviewJobStopReason.BudgetCapReached),
            JobStatus.Cancelled => ReviewJobLeaseRenewal.StoppedBecause(ReviewJobStopReason.OperatorStop),
            _ => ReviewJobLeaseRenewal.Rejected,
        };
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

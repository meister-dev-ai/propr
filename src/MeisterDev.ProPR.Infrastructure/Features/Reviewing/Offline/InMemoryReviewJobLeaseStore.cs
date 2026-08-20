// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     Claiming and liveness for the offline evaluation path, which runs reviews in one process against the
///     in-memory job store. There is no second claimant to race, so a lock around the same state transition
///     the database performs conditionally is enough to keep the semantics identical from the caller's side.
/// </summary>
public sealed class InMemoryReviewJobLeaseStore(
    InMemoryReviewJobRepository jobs,
    IOptions<ReviewLeaseOptions> leaseOptions) : IReviewJobLeaseStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);

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

        await this._gate.WaitAsync(ct);
        try
        {
            var job = jobs.GetById(jobId);
            if (job is null || job.Status != JobStatus.Pending)
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            job.Status = JobStatus.Processing;
            job.ProcessingStartedAt = now;
            // A claim starts a fresh attempt; a publication stamp left by an interrupted earlier one would
            // have the timeout sweep fail this attempt for something it never did.
            job.ClearPublishing();
            var expiresAt = now + leaseDuration;
            job.ApplyLease(owner, job.LeaseGeneration + 1, expiresAt, now);
            return new ReviewJobLease(jobId, owner, job.LeaseGeneration, expiresAt);
        }
        finally
        {
            this._gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ReviewJobLeaseRenewal> TryRenewAsync(
        ReviewJobLease lease,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);

        await this._gate.WaitAsync(ct);
        try
        {
            var job = jobs.GetById(lease.JobId);
            if (!IsHeldBy(job, lease))
            {
                return ExplainRefusal(job, lease);
            }

            // The same ceiling the durable store applies. An execution continues only while its renewals
            // succeed, so one that has run past the ceiling is refused its renewal and failed with that
            // reason.
            // The comparison is inclusive, matching the durable store: its predicate renews only while
            // processing_started_at is strictly newer than now minus the ceiling, so an execution sitting
            // exactly on the boundary is stopped there too.
            if (job!.ProcessingStartedAt is { } processingStartedAt
                && DateTimeOffset.UtcNow - processingStartedAt >= leaseOptions.Value.MaxReviewDuration)
            {
                // Conditional on the job still processing, as the durable store's statement is. The review
                // runs on another thread and can reach its own end state while this decides to stop it; an
                // unconditional failure would overwrite a review that had just completed, and the lease
                // clearing and the stop directive below would then be reported about a job this call did not
                // fail.
                var failed = jobs.TryFailWhileProcessing(
                    lease.JobId,
                    $"The review ran longer than the {leaseOptions.Value.MaxReviewDurationMinutes}-minute ceiling "
                    + "on one execution and was stopped.",
                    ReviewJobFailureReason.MaxDurationExceeded);
                if (!failed)
                {
                    // The durable statement matches no row in this case and the renewal is refused without a
                    // write. The job is already terminal, so the execution stops on its own next checkpoint.
                    return ReviewJobLeaseRenewal.Rejected;
                }

                // The durable repository clears the lease as part of failing a job, and a terminal job is
                // documented as holding none. The in-memory repository sets the status only, so the lease and
                // the publication marker are cleared here to match.
                job.ClearLease();
                job.ClearPublishing();
                return ReviewJobLeaseRenewal.StoppedBecause(ReviewJobStopReason.MaxDurationExceeded);
            }

            var now = DateTimeOffset.UtcNow;
            var expiresAt = now + leaseDuration;
            job!.ApplyLease(lease.Owner, lease.Generation, expiresAt, now);
            return new ReviewJobLeaseRenewal(true, expiresAt);
        }
        finally
        {
            this._gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryReleaseAsync(ReviewJobLease lease, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);

        await this._gate.WaitAsync(ct);
        try
        {
            var job = jobs.GetById(lease.JobId);
            if (!IsHeldBy(job, lease))
            {
                return false;
            }

            job!.Status = JobStatus.Pending;
            job.ClearLease();
            job.ClearPublishing();
            return true;
        }
        finally
        {
            this._gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task ClearLeaseAsync(Guid jobId, CancellationToken ct = default)
    {
        await this._gate.WaitAsync(ct);
        try
        {
            jobs.GetById(jobId)?.ClearLease();
        }
        finally
        {
            this._gate.Release();
        }
    }

    /// <inheritdoc />
    public Task<bool> IsLeaseCurrentAsync(ReviewJobLease lease, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return Task.FromResult(IsHeldBy(jobs.GetById(lease.JobId), lease));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ExpiredReviewJobLease>> GetExpiredLeasesAsync(
        int limit,
        TimeSpan reclaimBackoff,
        TimeSpan publicationTimeout,
        CancellationToken ct = default)
    {
        // Offline evaluation runs one review at a time in one process, so nothing can be abandoned while
        // the harness is alive and there is never anything to take back.
        return Task.FromResult<IReadOnlyList<ExpiredReviewJobLease>>([]);
    }

    /// <inheritdoc />
    public Task<ReviewJobReclaimOutcome> TryReclaimAsync(
        ExpiredReviewJobLease expired,
        int maxConsecutiveReclaims,
        int maxTotalReclaims,
        CancellationToken ct = default)
    {
        return Task.FromResult(ReviewJobReclaimOutcome.NotReclaimed);
    }

    /// <inheritdoc />
    public async Task<ReviewJobReclaimOutcome> TryReleaseFailedAsync(
        ReviewJobLease lease,
        int maxConsecutiveReclaims,
        int maxTotalReclaims,
        CancellationToken ct = default)
    {
        // The harness keeps no reclaim budget, like TryReclaimAsync above: the release still happens so
        // the job is claimable again, and the counting stays a property of the durable store.
        var released = await this.TryReleaseAsync(lease, ct);
        return released ? ReviewJobReclaimOutcome.Requeued : ReviewJobReclaimOutcome.NotReclaimed;
    }

    /// <inheritdoc />
    public async Task<bool> TryMarkPublishingAsync(
        Guid jobId,
        ReviewJobLease? lease = null,
        CancellationToken ct = default)
    {
        await this._gate.WaitAsync(ct);
        try
        {
            var job = jobs.GetById(jobId);
            if (job is null || job.Status != JobStatus.Processing)
            {
                return false;
            }

            if (lease is not null && !IsHeldBy(job, lease))
            {
                return false;
            }

            job.MarkPublishingStarted(DateTimeOffset.UtcNow);
            return true;
        }
        finally
        {
            this._gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task ClearPublishingAsync(Guid jobId, CancellationToken ct = default)
    {
        await this._gate.WaitAsync(ct);
        try
        {
            jobs.GetById(jobId)?.ClearPublishing();
        }
        finally
        {
            this._gate.Release();
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Guid>> FailTimedOutPublicationsAsync(
        int limit,
        TimeSpan publicationTimeout,
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<Guid>>([]);
    }

    private static ReviewJobLeaseRenewal ExplainRefusal(ReviewJob? job, ReviewJobLease lease)
    {
        if (job is null || job.LeaseGeneration != lease.Generation)
        {
            return ReviewJobLeaseRenewal.Rejected;
        }

        // A terminal state clears the owner, so only a still-executing job can be checked against it.
        if (job.Status == JobStatus.Processing
            && !string.Equals(job.LeaseOwner, lease.Owner, StringComparison.Ordinal))
        {
            return ReviewJobLeaseRenewal.Rejected;
        }

        return job.Status switch
        {
            JobStatus.Stopped or JobStatus.Cancelled =>
                ReviewJobLeaseRenewal.StoppedBecause(ReviewJobStopReason.OperatorStop),
            JobStatus.Superseded => ReviewJobLeaseRenewal.StoppedBecause(ReviewJobStopReason.Superseded),
            JobStatus.BudgetExceeded => ReviewJobLeaseRenewal.StoppedBecause(ReviewJobStopReason.BudgetCapReached),
            _ => ReviewJobLeaseRenewal.Rejected,
        };
    }

    private static bool IsHeldBy(ReviewJob? job, ReviewJobLease lease)
    {
        return job is not null
               && job.Status == JobStatus.Processing
               && job.LeaseGeneration == lease.Generation
               && string.Equals(job.LeaseOwner, lease.Owner, StringComparison.Ordinal);
    }
}

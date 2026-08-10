// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     Claiming and liveness for the offline evaluation path, which runs reviews in one process against the
///     in-memory job store. There is no second claimant to race, so a lock around the same state transition
///     the database performs conditionally is enough to keep the semantics identical from the caller's side.
/// </summary>
public sealed class InMemoryReviewJobLeaseStore(InMemoryReviewJobRepository jobs) : IReviewJobLeaseStore
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

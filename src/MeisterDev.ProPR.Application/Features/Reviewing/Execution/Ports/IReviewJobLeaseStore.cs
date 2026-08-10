// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Claiming and liveness boundary for review-job execution. A claim is a single conditional write, so
///     exactly one party wins it no matter how many are asking, and the claim stamps a lease whose holder
///     keeps it alive by renewing it. Liveness is that renewal, never elapsed processing time, which is what
///     lets one long review and one abandoned review be told apart.
/// </summary>
public interface IReviewJobLeaseStore
{
    /// <summary>
    ///     Reads up to <paramref name="limit" /> jobs eligible to be claimed, oldest first. Candidates only:
    ///     another party may claim any of them before the caller does, which the claim itself resolves.
    /// </summary>
    /// <param name="limit">Maximum number of candidates to return. Bounds the work one poll cycle can do.</param>
    /// <param name="submittedAfter">
    ///     Continues from an earlier window's last candidate. A caller whose whole window was ineligible
    ///     pages deeper with this instead of starving whatever sits behind it.
    /// </param>
    /// <param name="ct">The cancellation token.</param>
    Task<IReadOnlyList<ReviewJob>> GetClaimCandidatesAsync(
        int limit,
        DateTimeOffset? submittedAfter = null,
        CancellationToken ct = default);

    /// <summary>
    ///     Attempts to claim a specific job, stamping owner, a fresh generation, and an expiry in the same
    ///     conditional update that moves the status. Returns the granted lease, or null when someone else
    ///     won the job or it is no longer claimable. Losing is a clean no-op, not an error.
    /// </summary>
    /// <param name="jobId">The job to claim.</param>
    /// <param name="owner">Identity to stamp as the holder.</param>
    /// <param name="leaseDuration">How long the lease is granted for before it must be renewed.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<ReviewJobLease?> TryClaimAsync(
        Guid jobId,
        string owner,
        TimeSpan leaseDuration,
        CancellationToken ct = default);

    /// <summary>
    ///     Extends the lease's expiry when the caller still holds it. A caller whose generation is stale, who
    ///     is not the recorded owner, or whose job has since reached a terminal state is rejected and the
    ///     expiry is left untouched.
    /// </summary>
    /// <param name="lease">The lease the caller believes it holds.</param>
    /// <param name="leaseDuration">How far ahead to move the expiry.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<ReviewJobLeaseRenewal> TryRenewAsync(
        ReviewJobLease lease,
        TimeSpan leaseDuration,
        CancellationToken ct = default);

    /// <summary>
    ///     Releases the lease deliberately and returns the job to the claimable pool. Used by a planned
    ///     shutdown, drain, or scale-in, and distinguished from an expiry so it never counts against the
    ///     job as an abandonment.
    /// </summary>
    /// <param name="lease">The lease to release.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns><c>true</c> when the lease was still held and has been released.</returns>
    Task<bool> TryReleaseAsync(ReviewJobLease lease, CancellationToken ct = default);

    /// <summary>
    ///     Clears the lease from a job that has reached a terminal state, so nothing continues to look
    ///     leased once it is finished. Leaves the generation intact so a holder that comes back stays stale.
    /// </summary>
    /// <param name="jobId">The job whose lease to clear.</param>
    /// <param name="ct">The cancellation token.</param>
    Task ClearLeaseAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    ///     Reports whether the caller's lease is still the current one. The generation is the fencing token:
    ///     a holder that was paused past its expiry and reclaimed by someone else fails this check.
    /// </summary>
    /// <param name="lease">The lease the caller believes it holds.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<bool> IsLeaseCurrentAsync(ReviewJobLease lease, CancellationToken ct = default);

    /// <summary>
    ///     Finds jobs whose lease has expired and that are eligible to be taken back. Jobs that are
    ///     publishing are excluded until their own, longer timeout passes, and a job reclaimed within the
    ///     backoff window is left alone so a mass expiry does not turn into a reclaim storm.
    /// </summary>
    /// <param name="limit">Maximum number of jobs to return, bounding the burst one sweep can cause.</param>
    /// <param name="reclaimBackoff">How long after a reclaim a job is left alone before being offered again.</param>
    /// <param name="publicationTimeout">How long a publishing job is protected from reclaim.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<IReadOnlyList<ExpiredReviewJobLease>> GetExpiredLeasesAsync(
        int limit,
        TimeSpan reclaimBackoff,
        TimeSpan publicationTimeout,
        CancellationToken ct = default);

    /// <summary>
    ///     Takes back a job whose lease expired, in one conditional write, so several hosts racing to reclaim
    ///     the same job produce exactly one winner. The job returns to the pending pool unless doing so would
    ///     exceed its reclaim budget, in which case it is failed with a reason naming the lease loss.
    /// </summary>
    /// <param name="expired">The expired lease, carrying the generation the caller observed.</param>
    /// <param name="maxConsecutiveReclaims">Reclaims allowed without new per-file progress.</param>
    /// <param name="maxTotalReclaims">Reclaims allowed in total, whatever the progress.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<ReviewJobReclaimOutcome> TryReclaimAsync(
        ExpiredReviewJobLease expired,
        int maxConsecutiveReclaims,
        int maxTotalReclaims,
        CancellationToken ct = default);

    /// <summary>
    ///     Releases a lease after a failed attempt, spending one of the job's reclaim attempts on the way
    ///     back to the pool. A clean handback after a failure that cost the job nothing let a host fail the
    ///     same job forever: crash and expiry were bounded by the reclaim budget, deliberate failure was
    ///     not, and the two are the same event to the job.
    /// </summary>
    /// <param name="lease">The lease being handed back by the party that failed.</param>
    /// <param name="maxConsecutiveReclaims">Attempts allowed without new per-file progress.</param>
    /// <param name="maxTotalReclaims">Attempts allowed in total, whatever the progress.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<ReviewJobReclaimOutcome> TryReleaseFailedAsync(
        ReviewJobLease lease,
        int maxConsecutiveReclaims,
        int maxTotalReclaims,
        CancellationToken ct = default);

    /// <summary>
    ///     Marks the job as publishing, which protects it from reclaim while outbound comments are going out.
    /// </summary>
    /// <param name="jobId">The job entering publication.</param>
    /// <param name="lease">
    ///     The caller's lease when it has one to present, in which case the mark is refused unless the lease
    ///     is still current. The in-process path passes none: it is already running inside the holder, and a
    ///     party that lost its lease can at worst delay a reclaim until the publication timeout passes.
    /// </param>
    /// <param name="ct">The cancellation token.</param>
    Task<bool> TryMarkPublishingAsync(Guid jobId, ReviewJobLease? lease = null, CancellationToken ct = default);

    /// <summary>Records that publication has finished, so the job is no longer protected as publishing.</summary>
    Task ClearPublishingAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    ///     Fails a job whose publication began and never finished. Separate from a reclaim because comments
    ///     may already be posted, so the job needs a human rather than another attempt.
    /// </summary>
    /// <param name="limit">Maximum number of jobs to fail in one sweep.</param>
    /// <param name="publicationTimeout">How long publication may run before it counts as stuck.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The jobs that were failed.</returns>
    Task<IReadOnlyList<Guid>> FailTimedOutPublicationsAsync(
        int limit,
        TimeSpan publicationTimeout,
        CancellationToken ct = default);
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Persistence abstraction for durable ProCursor index jobs.
/// </summary>
public interface IProCursorIndexJobRepository
{
    /// <summary>Persists a newly created durable index job.</summary>
    Task AddAsync(ProCursorIndexJob job, CancellationToken ct = default);

    /// <summary>Returns one durable index job by identifier.</summary>
    Task<ProCursorIndexJob?> GetByIdAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Returns the next pending job available for worker pickup.</summary>
    Task<ProCursorIndexJob?> GetNextPendingAsync(CancellationToken ct = default);

    /// <summary>Returns the next pending job whose source is not in the excluded set.</summary>
    Task<ProCursorIndexJob?> GetNextPendingAsync(
        IReadOnlyCollection<Guid> excludedSourceIds,
        CancellationToken ct = default);

    /// <summary>Returns active jobs for one knowledge source.</summary>
    Task<IReadOnlyList<ProCursorIndexJob>> ListActiveAsync(Guid knowledgeSourceId, CancellationToken ct = default);

    /// <summary>Returns whether a non-terminal job already exists for the deduplication key.</summary>
    Task<bool> HasActiveJobAsync(Guid trackedBranchId, string dedupKey, CancellationToken ct = default);

    /// <summary>
    ///     Returns the jobs in processing whose lease expired before <paramref name="now" />. A job that was started
    ///     before the lease was recorded has no lease and is returned as well.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <param name="excludedJobIds">The jobs that the calling service runs itself.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<ProCursorIndexJob>> ListExpiredLeasesAsync(
        DateTimeOffset now,
        IReadOnlyCollection<Guid> excludedJobIds,
        CancellationToken ct = default);

    /// <summary>
    ///     Persists the recovery of a job whose lease expired: its return to the queue or its failure. The write takes
    ///     effect only while the stored job is still in processing under the same attempt and its lease is still
    ///     expired at <paramref name="now" />.
    /// </summary>
    /// <param name="job">The job, already returned to the queue or failed.</param>
    /// <param name="now">The time that the lease expired before.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     False when the service that runs the job renewed the lease after the job was read, or when the job was
    ///     cancelled, ended or claimed again. Nothing is written then.
    /// </returns>
    Task<bool> RecoverExpiredLeaseAsync(ProCursorIndexJob job, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Persists state changes to an existing index job.</summary>
    /// <returns>
    ///     False when the stored job no longer has the status and attempt count that were read, because another
    ///     service claimed or recovered it or the job was cancelled. Nothing is written then.
    /// </returns>
    Task<bool> UpdateAsync(ProCursorIndexJob job, CancellationToken ct = default);
}

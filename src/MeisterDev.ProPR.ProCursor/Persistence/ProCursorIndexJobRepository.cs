// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.ProCursor.Persistence;

/// <summary>
///     EF Core implementation of <see cref="IProCursorIndexJobRepository" />.
/// </summary>
public sealed class ProCursorIndexJobRepository(ProCursorOperationalDbContext db) : IProCursorIndexJobRepository
{
    /// <inheritdoc />
    public async Task AddAsync(ProCursorIndexJob job, CancellationToken ct = default)
    {
        db.ProCursorIndexJobs.Add(job);
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<ProCursorIndexJob?> GetByIdAsync(Guid jobId, CancellationToken ct = default)
    {
        return await db.ProCursorIndexJobs.FirstOrDefaultAsync(job => job.Id == jobId, ct);
    }

    /// <inheritdoc />
    public async Task<ProCursorIndexJob?> GetNextPendingAsync(CancellationToken ct = default)
    {
        return await db.ProCursorIndexJobs
            .Where(job => job.Status == ProCursorIndexJobStatus.Pending)
            .OrderBy(job => job.QueuedAt)
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc />
    public async Task<ProCursorIndexJob?> GetNextPendingAsync(
        IReadOnlyCollection<Guid> excludedSourceIds,
        CancellationToken ct = default)
    {
        var query = db.ProCursorIndexJobs
            .Where(job => job.Status == ProCursorIndexJobStatus.Pending);

        if (excludedSourceIds.Count > 0)
        {
            query = query.Where(job => !excludedSourceIds.Contains(job.KnowledgeSourceId));
        }

        return await query
            .OrderBy(job => job.QueuedAt)
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProCursorIndexJob>> ListActiveAsync(
        Guid knowledgeSourceId,
        CancellationToken ct = default)
    {
        return await db.ProCursorIndexJobs
            .Where(job =>
                job.KnowledgeSourceId == knowledgeSourceId &&
                (job.Status == ProCursorIndexJobStatus.Pending || job.Status == ProCursorIndexJobStatus.Processing))
            .OrderBy(job => job.QueuedAt)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<bool> HasActiveJobAsync(Guid trackedBranchId, string dedupKey, CancellationToken ct = default)
    {
        return await db.ProCursorIndexJobs.AnyAsync(
            job =>
                job.TrackedBranchId == trackedBranchId &&
                job.DedupKey == dedupKey &&
                (job.Status == ProCursorIndexJobStatus.Pending || job.Status == ProCursorIndexJobStatus.Processing),
            ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProCursorIndexJob>> ListExpiredLeasesAsync(
        DateTimeOffset now,
        IReadOnlyCollection<Guid> excludedJobIds,
        CancellationToken ct = default)
    {
        return await db.ProCursorIndexJobs
            .Where(job =>
                job.Status == ProCursorIndexJobStatus.Processing &&
                (job.LeaseExpiresAt == null || job.LeaseExpiresAt < now) &&
                !excludedJobIds.Contains(job.Id))
            .OrderBy(job => job.QueuedAt)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<bool> RecoverExpiredLeaseAsync(ProCursorIndexJob job, DateTimeOffset now, CancellationToken ct = default)
    {
        // A renewal changes only the lease, which is no concurrency token. The condition on the lease in the same
        // statement keeps the recovery from taking a job whose lease was renewed after it was read.
        var updated = await db.ProCursorIndexJobs
            .Where(stored =>
                stored.Id == job.Id &&
                stored.Status == ProCursorIndexJobStatus.Processing &&
                stored.AttemptCount == job.AttemptCount &&
                (stored.LeaseExpiresAt == null || stored.LeaseExpiresAt < now))
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(stored => stored.Status, job.Status)
                    .SetProperty(stored => stored.StartedAt, job.StartedAt)
                    .SetProperty(stored => stored.CompletedAt, job.CompletedAt)
                    .SetProperty(stored => stored.FailureReason, job.FailureReason)
                    .SetProperty(stored => stored.LeaseExpiresAt, job.LeaseExpiresAt),
                ct);

        // The statement bypasses the change tracker, so a later query of this context reads the stored job.
        db.Entry(job).State = EntityState.Detached;
        return updated == 1;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(ProCursorIndexJob job, CancellationToken ct = default)
    {
        db.ProCursorIndexJobs.Update(job);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // The status and the attempt count are concurrency tokens. The job is detached, so a later query of this
            // context reads the stored job.
            db.Entry(job).State = EntityState.Detached;
            return false;
        }
    }
}

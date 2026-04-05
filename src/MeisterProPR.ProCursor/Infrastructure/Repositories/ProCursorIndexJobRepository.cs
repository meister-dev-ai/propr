// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>
///     EF Core implementation of <see cref="IProCursorIndexJobRepository" />.
/// </summary>
public sealed class ProCursorIndexJobRepository(MeisterProPRDbContext db) : IProCursorIndexJobRepository
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
    public async Task<ProCursorIndexJob?> GetNextPendingAsync(IReadOnlyCollection<Guid> excludedSourceIds, CancellationToken ct = default)
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
    public async Task<IReadOnlyList<ProCursorIndexJob>> ListActiveAsync(Guid knowledgeSourceId, CancellationToken ct = default)
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
        return await db.ProCursorIndexJobs.AnyAsync(job =>
            job.TrackedBranchId == trackedBranchId &&
            job.DedupKey == dedupKey &&
            (job.Status == ProCursorIndexJobStatus.Pending || job.Status == ProCursorIndexJobStatus.Processing),
            ct);
    }

    /// <inheritdoc />
    public async Task UpdateAsync(ProCursorIndexJob job, CancellationToken ct = default)
    {
        db.ProCursorIndexJobs.Update(job);
        await db.SaveChangesAsync(ct);
    }
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs.ProCursor;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.ProCursor.Persistence;

/// <summary>Stops and deletes the ProCursor data of knowledge sources and clients in the ProCursor database.</summary>
public sealed class ProCursorTenantData(ProCursorOperationalDbContext db) : IProCursorTenantData
{
    public async Task<ProCursorIndexingStop> StopIndexingAsync(
        IReadOnlyCollection<Guid> knowledgeSourceIds,
        CancellationToken ct = default)
    {
        var jobs = db.ProCursorIndexJobs.Where(job => knowledgeSourceIds.Contains(job.KnowledgeSourceId));
        var cancelled = await jobs
            .Where(job => job.Status == ProCursorIndexJobStatus.Pending)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(job => job.Status, ProCursorIndexJobStatus.Cancelled)
                    .SetProperty(job => job.CompletedAt, DateTimeOffset.UtcNow),
                ct);

        // A running index job is left to finish. Cancelled, it would fail only when it completes, after it has written
        // its snapshot.
        var runningSince = await jobs
            .Where(job => job.Status == ProCursorIndexJobStatus.Processing)
            .Select(job => job.StartedAt ?? job.QueuedAt)
            .ToListAsync(ct);
        return new ProCursorIndexingStop(cancelled, runningSince);
    }

    public async Task<IReadOnlyDictionary<string, int>> DeleteAsync(
        IReadOnlyCollection<Guid> knowledgeSourceIds,
        IReadOnlyCollection<Guid> clientIds,
        CancellationToken ct = default)
    {
        var deleted = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var snapshots = db.ProCursorIndexSnapshots
            .Where(snapshot => knowledgeSourceIds.Contains(snapshot.KnowledgeSourceId))
            .Select(snapshot => snapshot.Id);
        await Remove(db.ProCursorKnowledgeChunks.Where(chunk => snapshots.Contains(chunk.SnapshotId)));
        await Remove(db.ProCursorSymbolEdges.Where(edge => snapshots.Contains(edge.SnapshotId)));
        await Remove(db.ProCursorSymbolRecords.Where(symbol => snapshots.Contains(symbol.SnapshotId)));
        await Remove(db.ProCursorIndexSnapshots.Where(snapshot => knowledgeSourceIds.Contains(snapshot.KnowledgeSourceId)));
        await Remove(db.ProCursorIndexJobs.Where(job => knowledgeSourceIds.Contains(job.KnowledgeSourceId)));
        await Remove(
            db.ProCursorTokenUsageEvents.Where(usage =>
                clientIds.Contains(usage.ClientId) || knowledgeSourceIds.Contains(usage.ProCursorSourceId)));
        await Remove(db.ProCursorTokenUsageRollups.Where(rollup => clientIds.Contains(rollup.ClientId)));
        await transaction.CommitAsync(ct);
        return deleted;

        async Task Remove<T>(IQueryable<T> rows)
            where T : class
        {
            deleted[db.Model.FindEntityType(typeof(T))!.GetTableName()!] = await rows.ExecuteDeleteAsync(ct);
        }
    }
}

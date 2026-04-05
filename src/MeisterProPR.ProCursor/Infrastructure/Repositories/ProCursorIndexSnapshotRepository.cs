// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>
///     EF Core implementation of <see cref="IProCursorIndexSnapshotRepository" />.
/// </summary>
public sealed class ProCursorIndexSnapshotRepository(MeisterProPRDbContext db) : IProCursorIndexSnapshotRepository
{
    /// <inheritdoc />
    public async Task AddAsync(ProCursorIndexSnapshot snapshot, CancellationToken ct = default)
    {
        db.ProCursorIndexSnapshots.Add(snapshot);
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<ProCursorIndexSnapshot?> GetByIdAsync(Guid snapshotId, CancellationToken ct = default)
    {
        return await db.ProCursorIndexSnapshots.FirstOrDefaultAsync(snapshot => snapshot.Id == snapshotId, ct);
    }

    /// <inheritdoc />
    public async Task<ProCursorIndexSnapshot?> GetLatestAsync(
        Guid knowledgeSourceId,
        Guid? trackedBranchId = null,
        CancellationToken ct = default)
    {
        var query = db.ProCursorIndexSnapshots
            .Where(snapshot => snapshot.KnowledgeSourceId == knowledgeSourceId);

        if (trackedBranchId.HasValue)
        {
            query = query.Where(snapshot => snapshot.TrackedBranchId == trackedBranchId.Value);
        }

        return await query
            .OrderByDescending(snapshot => snapshot.CreatedAt)
            .ThenByDescending(snapshot => snapshot.CompletedAt)
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc />
    public async Task<ProCursorIndexSnapshot?> GetLatestReadyAsync(
        Guid knowledgeSourceId,
        Guid? trackedBranchId = null,
        CancellationToken ct = default)
    {
        var query = db.ProCursorIndexSnapshots
            .Where(snapshot => snapshot.KnowledgeSourceId == knowledgeSourceId && snapshot.Status == "ready");

        if (trackedBranchId.HasValue)
        {
            query = query.Where(snapshot => snapshot.TrackedBranchId == trackedBranchId.Value);
        }

        return await query
            .OrderByDescending(snapshot => snapshot.CompletedAt)
            .ThenByDescending(snapshot => snapshot.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProCursorKnowledgeChunk>> ListKnowledgeChunksAsync(Guid snapshotId, CancellationToken ct = default)
    {
        return await db.ProCursorKnowledgeChunks
            .Where(chunk => chunk.SnapshotId == snapshotId)
            .OrderBy(chunk => chunk.SourcePath)
            .ThenBy(chunk => chunk.ChunkOrdinal)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProCursorIndexSnapshot>> ListBySourceAsync(Guid knowledgeSourceId, CancellationToken ct = default)
    {
        return await db.ProCursorIndexSnapshots
            .Where(snapshot => snapshot.KnowledgeSourceId == knowledgeSourceId)
            .OrderByDescending(snapshot => snapshot.CreatedAt)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task ReplaceKnowledgeChunksAsync(
        Guid snapshotId,
        IReadOnlyList<ProCursorKnowledgeChunk> chunks,
        CancellationToken ct = default)
    {
        if (db.Database.IsRelational())
        {
            await db.ProCursorKnowledgeChunks
                .Where(chunk => chunk.SnapshotId == snapshotId)
                .ExecuteDeleteAsync(ct);
        }
        else
        {
            var existingChunks = await db.ProCursorKnowledgeChunks
                .Where(chunk => chunk.SnapshotId == snapshotId)
                .ToListAsync(ct);

            if (existingChunks.Count > 0)
            {
                db.ProCursorKnowledgeChunks.RemoveRange(existingChunks);
            }
        }

        if (chunks.Count > 0)
        {
            db.ProCursorKnowledgeChunks.AddRange(chunks);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task ReplaceSymbolGraphAsync(
        Guid snapshotId,
        IReadOnlyList<ProCursorSymbolRecord> symbols,
        IReadOnlyList<ProCursorSymbolEdge> edges,
        CancellationToken ct = default)
    {
        await db.ProCursorSymbolEdges
            .Where(edge => edge.SnapshotId == snapshotId)
            .ExecuteDeleteAsync(ct);
        await db.ProCursorSymbolRecords
            .Where(symbol => symbol.SnapshotId == snapshotId)
            .ExecuteDeleteAsync(ct);

        if (symbols.Count > 0)
        {
            db.ProCursorSymbolRecords.AddRange(symbols);
        }

        if (edges.Count > 0)
        {
            db.ProCursorSymbolEdges.AddRange(edges);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task UpdateAsync(ProCursorIndexSnapshot snapshot, CancellationToken ct = default)
    {
        db.ProCursorIndexSnapshots.Update(snapshot);
        await db.SaveChangesAsync(ct);
    }
}

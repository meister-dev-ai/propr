// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Persistence abstraction for ProCursor index snapshots and their persisted content.
/// </summary>
public interface IProCursorIndexSnapshotRepository
{
    /// <summary>Persists a newly created snapshot shell.</summary>
    Task AddAsync(ProCursorIndexSnapshot snapshot, CancellationToken ct = default);

    /// <summary>Returns one snapshot by identifier.</summary>
    Task<ProCursorIndexSnapshot?> GetByIdAsync(Guid snapshotId, CancellationToken ct = default);

    /// <summary>Returns the latest snapshot for a source or one tracked branch regardless of status.</summary>
    Task<ProCursorIndexSnapshot?> GetLatestAsync(
        Guid knowledgeSourceId,
        Guid? trackedBranchId = null,
        CancellationToken ct = default);

    /// <summary>Returns the latest ready snapshot for a source or one tracked branch.</summary>
    Task<ProCursorIndexSnapshot?> GetLatestReadyAsync(
        Guid knowledgeSourceId,
        Guid? trackedBranchId = null,
        CancellationToken ct = default);

    /// <summary>Returns the persisted knowledge chunks belonging to one ready snapshot.</summary>
    Task<IReadOnlyList<ProCursorKnowledgeChunk>> ListKnowledgeChunksAsync(Guid snapshotId, CancellationToken ct = default);

    /// <summary>Lists snapshots for a knowledge source.</summary>
    Task<IReadOnlyList<ProCursorIndexSnapshot>> ListBySourceAsync(Guid knowledgeSourceId, CancellationToken ct = default);

    /// <summary>Replaces the persisted chunk set for a snapshot.</summary>
    Task ReplaceKnowledgeChunksAsync(
        Guid snapshotId,
        IReadOnlyList<ProCursorKnowledgeChunk> chunks,
        CancellationToken ct = default);

    /// <summary>Replaces the persisted symbol graph for a snapshot.</summary>
    Task ReplaceSymbolGraphAsync(
        Guid snapshotId,
        IReadOnlyList<ProCursorSymbolRecord> symbols,
        IReadOnlyList<ProCursorSymbolEdge> edges,
        CancellationToken ct = default);

    /// <summary>Persists state changes to an existing snapshot.</summary>
    Task UpdateAsync(ProCursorIndexSnapshot snapshot, CancellationToken ct = default);
}

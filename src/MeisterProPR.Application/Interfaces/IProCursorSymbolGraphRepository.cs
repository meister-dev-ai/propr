// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Persistence abstraction for persisted ProCursor symbol graphs and symbol lookups.
/// </summary>
public interface IProCursorSymbolGraphRepository
{
    /// <summary>Replaces all symbol definitions and edges for one snapshot.</summary>
    Task ReplaceAsync(
        Guid snapshotId,
        IReadOnlyList<ProCursorSymbolRecord> symbols,
        IReadOnlyList<ProCursorSymbolEdge> edges,
        CancellationToken ct = default);

    /// <summary>Returns one persisted symbol record by its snapshot-local key.</summary>
    Task<ProCursorSymbolRecord?> GetBySymbolKeyAsync(Guid snapshotId, string symbolKey, CancellationToken ct = default);

    /// <summary>Searches the persisted symbol set for one snapshot.</summary>
    Task<IReadOnlyList<ProCursorSymbolRecord>> SearchAsync(
        Guid snapshotId,
        string queryText,
        int maxResults,
        CancellationToken ct = default);

    /// <summary>Returns relationship edges related to the given symbol key.</summary>
    Task<IReadOnlyList<ProCursorSymbolEdge>> ListEdgesAsync(
        Guid snapshotId,
        string symbolKey,
        int maxRelations,
        CancellationToken ct = default);
}

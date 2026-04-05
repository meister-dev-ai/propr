// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>
///     EF Core repository for persisted ProCursor symbol graphs.
/// </summary>
public sealed class ProCursorSymbolGraphRepository(MeisterProPRDbContext db) : IProCursorSymbolGraphRepository
{
    /// <summary>
    ///     Replaces all persisted symbol definitions and edges for one snapshot.
    /// </summary>
    public async Task ReplaceAsync(
        Guid snapshotId,
        IReadOnlyList<ProCursorSymbolRecord> symbols,
        IReadOnlyList<ProCursorSymbolEdge> edges,
        CancellationToken ct = default)
    {
        if (db.Database.IsRelational())
        {
            await db.ProCursorSymbolEdges
                .Where(edge => edge.SnapshotId == snapshotId)
                .ExecuteDeleteAsync(ct);
            await db.ProCursorSymbolRecords
                .Where(symbol => symbol.SnapshotId == snapshotId)
                .ExecuteDeleteAsync(ct);
        }
        else
        {
            var existingEdges = await db.ProCursorSymbolEdges
                .Where(edge => edge.SnapshotId == snapshotId)
                .ToListAsync(ct);
            var existingSymbols = await db.ProCursorSymbolRecords
                .Where(symbol => symbol.SnapshotId == snapshotId)
                .ToListAsync(ct);

            if (existingEdges.Count > 0)
            {
                db.ProCursorSymbolEdges.RemoveRange(existingEdges);
            }

            if (existingSymbols.Count > 0)
            {
                db.ProCursorSymbolRecords.RemoveRange(existingSymbols);
            }
        }

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

    /// <summary>
    ///     Returns one persisted symbol record by its snapshot-local key.
    /// </summary>
    public Task<ProCursorSymbolRecord?> GetBySymbolKeyAsync(Guid snapshotId, string symbolKey, CancellationToken ct = default)
    {
        return db.ProCursorSymbolRecords
            .FirstOrDefaultAsync(symbol => symbol.SnapshotId == snapshotId && symbol.SymbolKey == symbolKey, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProCursorSymbolRecord>> SearchAsync(
        Guid snapshotId,
        string queryText,
        int maxResults,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queryText);

        var normalizedQuery = queryText.Trim();
        var clampedMaxResults = Math.Max(1, maxResults);
        var symbols = await db.ProCursorSymbolRecords
            .Where(symbol => symbol.SnapshotId == snapshotId)
            .ToListAsync(ct);

        return symbols
            .Where(symbol => Matches(symbol, normalizedQuery))
            .OrderBy(symbol => GetMatchRank(symbol, normalizedQuery))
            .ThenBy(symbol => symbol.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(symbol => symbol.FilePath, StringComparer.OrdinalIgnoreCase)
            .Take(clampedMaxResults)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    ///     Returns relationship edges related to the given symbol key.
    /// </summary>
    public async Task<IReadOnlyList<ProCursorSymbolEdge>> ListEdgesAsync(
        Guid snapshotId,
        string symbolKey,
        int maxRelations,
        CancellationToken ct = default)
    {
        return await db.ProCursorSymbolEdges
            .Where(edge => edge.SnapshotId == snapshotId && (edge.FromSymbolKey == symbolKey || edge.ToSymbolKey == symbolKey))
            .OrderBy(edge => edge.FilePath)
            .ThenBy(edge => edge.LineStart)
            .Take(maxRelations)
            .ToListAsync(ct);
    }

    private static bool Matches(ProCursorSymbolRecord symbol, string queryText)
    {
        return string.Equals(symbol.SymbolKey, queryText, StringComparison.OrdinalIgnoreCase)
               || string.Equals(symbol.DisplayName, queryText, StringComparison.OrdinalIgnoreCase)
               || string.Equals(symbol.Signature, queryText, StringComparison.OrdinalIgnoreCase)
               || symbol.SymbolKey.Contains(queryText, StringComparison.OrdinalIgnoreCase)
               || symbol.DisplayName.Contains(queryText, StringComparison.OrdinalIgnoreCase)
               || symbol.Signature.Contains(queryText, StringComparison.OrdinalIgnoreCase)
               || symbol.SearchText.Contains(queryText, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetMatchRank(ProCursorSymbolRecord symbol, string queryText)
    {
        if (string.Equals(symbol.SymbolKey, queryText, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (string.Equals(symbol.Signature, queryText, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (string.Equals(symbol.DisplayName, queryText, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (symbol.Signature.StartsWith(queryText, StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (symbol.DisplayName.StartsWith(queryText, StringComparison.OrdinalIgnoreCase))
        {
            return 4;
        }

        return 5;
    }
}

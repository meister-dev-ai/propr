// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     Relationship edge observed between two symbols in one ProCursor snapshot.
/// </summary>
public sealed class ProCursorSymbolEdge
{
    public ProCursorSymbolEdge(
        Guid id,
        Guid snapshotId,
        string fromSymbolKey,
        string toSymbolKey,
        string edgeKind,
        string filePath,
        int? lineStart,
        int? lineEnd)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must not be empty.", nameof(id));
        }

        if (snapshotId == Guid.Empty)
        {
            throw new ArgumentException("SnapshotId must not be empty.", nameof(snapshotId));
        }

        if (lineStart.HasValue != lineEnd.HasValue)
        {
            throw new ArgumentException("LineStart and LineEnd must both be provided or both be null.");
        }

        if (lineStart.HasValue && lineStart.Value < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(lineStart));
        }

        if (lineEnd.HasValue && lineStart.HasValue && lineEnd.Value < lineStart.Value)
        {
            throw new ArgumentOutOfRangeException(nameof(lineEnd));
        }

        this.Id = id;
        this.SnapshotId = snapshotId;
        this.FromSymbolKey = NormalizeRequired(fromSymbolKey, nameof(fromSymbolKey));
        this.ToSymbolKey = NormalizeRequired(toSymbolKey, nameof(toSymbolKey));
        this.EdgeKind = NormalizeRequired(edgeKind, nameof(edgeKind));
        this.FilePath = NormalizeRequired(filePath, nameof(filePath));
        this.LineStart = lineStart;
        this.LineEnd = lineEnd;
    }

    private ProCursorSymbolEdge()
    {
    }

    public Guid Id { get; private set; }

    public Guid SnapshotId { get; private set; }

    public string FromSymbolKey { get; private set; } = string.Empty;

    public string ToSymbolKey { get; private set; } = string.Empty;

    public string EdgeKind { get; private set; } = string.Empty;

    public string FilePath { get; private set; } = string.Empty;

    public int? LineStart { get; private set; }

    public int? LineEnd { get; private set; }

    private static string NormalizeRequired(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value.Trim();
    }
}

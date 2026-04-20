// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     Relationship edge observed between two symbols in one ProCursor snapshot.
/// </summary>
public sealed class ProCursorSymbolEdge
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="ProCursorSymbolEdge"/> class.
    /// </summary>
    /// <param name="id">The unique identifier for the edge.</param>
    /// <param name="snapshotId">The identifier of the snapshot this edge belongs to.</param>
    /// <param name="fromSymbolKey">The key of the source symbol.</param>
    /// <param name="toSymbolKey">The key of the target symbol.</param>
    /// <param name="edgeKind">The kind of relationship between the symbols.</param>
    /// <param name="filePath">The file path associated with the edge.</param>
    /// <param name="lineStart">The starting line number, or null if not applicable.</param>
    /// <param name="lineEnd">The ending line number, or null if not applicable.</param>
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

    /// <summary>
    ///     Gets the unique identifier for the edge.
    /// </summary>
    public Guid Id { get; private set; }

    /// <summary>
    ///     Gets the identifier of the snapshot this edge belongs to.
    /// </summary>
    public Guid SnapshotId { get; private set; }

    /// <summary>
    ///     Gets the key of the source symbol.
    /// </summary>
    public string FromSymbolKey { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the key of the target symbol.
    /// </summary>
    public string ToSymbolKey { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the kind of relationship between the symbols.
    /// </summary>
    public string EdgeKind { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the file path associated with the edge.
    /// </summary>
    public string FilePath { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the starting line number, or null if not applicable.
    /// </summary>
    public int? LineStart { get; private set; }

    /// <summary>
    ///     Gets the ending line number, or null if not applicable.
    /// </summary>
    public int? LineEnd { get; private set; }

    private static string NormalizeRequired(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value.Trim();
    }
}

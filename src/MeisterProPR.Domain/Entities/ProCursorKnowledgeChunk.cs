// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     Searchable text unit used for lexical and semantic ProCursor retrieval.
/// </summary>
public sealed class ProCursorKnowledgeChunk
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="ProCursorKnowledgeChunk"/> class.
    /// </summary>
    /// <param name="id">The unique identifier for the knowledge chunk.</param>
    /// <param name="snapshotId">The unique identifier for the snapshot.</param>
    /// <param name="sourcePath">The source file path.</param>
    /// <param name="chunkKind">The kind of chunk.</param>
    /// <param name="title">The optional title of the chunk.</param>
    /// <param name="chunkOrdinal">The ordinal position of the chunk.</param>
    /// <param name="lineStart">The optional starting line number.</param>
    /// <param name="lineEnd">The optional ending line number.</param>
    /// <param name="contentHash">The hash of the content.</param>
    /// <param name="contentText">The text content of the chunk.</param>
    /// <param name="embeddingVector">The embedding vector.</param>
    public ProCursorKnowledgeChunk(
        Guid id,
        Guid snapshotId,
        string sourcePath,
        string chunkKind,
        string? title,
        int chunkOrdinal,
        int? lineStart,
        int? lineEnd,
        string contentHash,
        string contentText,
        float[] embeddingVector)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must not be empty.", nameof(id));
        }

        if (snapshotId == Guid.Empty)
        {
            throw new ArgumentException("SnapshotId must not be empty.", nameof(snapshotId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(chunkOrdinal);

        if (lineStart.HasValue && lineStart.Value < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(lineStart));
        }

        if (lineEnd.HasValue && lineStart.HasValue && lineEnd.Value < lineStart.Value)
        {
            throw new ArgumentOutOfRangeException(nameof(lineEnd));
        }

        if (embeddingVector.Length == 0)
        {
            throw new ArgumentException("EmbeddingVector must contain at least one value.", nameof(embeddingVector));
        }

        this.Id = id;
        this.SnapshotId = snapshotId;
        this.SourcePath = NormalizeRequired(sourcePath, nameof(sourcePath));
        this.ChunkKind = NormalizeRequired(chunkKind, nameof(chunkKind));
        this.Title = NormalizeOptional(title);
        this.ChunkOrdinal = chunkOrdinal;
        this.LineStart = lineStart;
        this.LineEnd = lineEnd;
        this.ContentHash = NormalizeRequired(contentHash, nameof(contentHash));
        this.ContentText = NormalizeRequired(contentText, nameof(contentText));
        this.EmbeddingVector = embeddingVector.ToArray();
        this.CreatedAt = DateTimeOffset.UtcNow;
    }

    private ProCursorKnowledgeChunk()
    {
        this.EmbeddingVector = [];
    }

    /// <summary>
    ///     Gets the unique identifier for the knowledge chunk.
    /// </summary>
    public Guid Id { get; private set; }

    /// <summary>
    ///     Gets the unique identifier for the snapshot.
    /// </summary>
    public Guid SnapshotId { get; private set; }

    /// <summary>
    ///     Gets the source file path.
    /// </summary>
    public string SourcePath { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the kind of chunk.
    /// </summary>
    public string ChunkKind { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the optional title of the chunk.
    /// </summary>
    public string? Title { get; private set; }

    /// <summary>
    ///     Gets the ordinal position of the chunk.
    /// </summary>
    public int ChunkOrdinal { get; private set; }

    /// <summary>
    ///     Gets the optional starting line number.
    /// </summary>
    public int? LineStart { get; private set; }

    /// <summary>
    ///     Gets the optional ending line number.
    /// </summary>
    public int? LineEnd { get; private set; }

    /// <summary>
    ///     Gets the hash of the content.
    /// </summary>
    public string ContentHash { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the text content of the chunk.
    /// </summary>
    public string ContentText { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the embedding vector.
    /// </summary>
    public float[] EmbeddingVector { get; private set; }

    /// <summary>
    ///     Gets the creation timestamp.
    /// </summary>
    public DateTimeOffset CreatedAt { get; private set; }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string NormalizeRequired(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value.Trim();
    }
}

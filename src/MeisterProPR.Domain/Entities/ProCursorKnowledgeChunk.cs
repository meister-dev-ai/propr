// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     Searchable text unit used for lexical and semantic ProCursor retrieval.
/// </summary>
public sealed class ProCursorKnowledgeChunk
{
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

        if (chunkOrdinal < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkOrdinal));
        }

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

    public Guid Id { get; private set; }

    public Guid SnapshotId { get; private set; }

    public string SourcePath { get; private set; } = string.Empty;

    public string ChunkKind { get; private set; } = string.Empty;

    public string? Title { get; private set; }

    public int ChunkOrdinal { get; private set; }

    public int? LineStart { get; private set; }

    public int? LineEnd { get; private set; }

    public string ContentHash { get; private set; } = string.Empty;

    public string ContentText { get; private set; } = string.Empty;

    public float[] EmbeddingVector { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeRequired(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value.Trim();
    }
}

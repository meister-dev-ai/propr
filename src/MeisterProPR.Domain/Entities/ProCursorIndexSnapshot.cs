// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     Versioned persisted ProCursor snapshot for one source branch at one commit.
/// </summary>
public sealed class ProCursorIndexSnapshot
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="ProCursorIndexSnapshot" /> class.
    /// </summary>
    /// <param name="id">The unique identifier for the snapshot.</param>
    /// <param name="knowledgeSourceId">The identifier of the knowledge source.</param>
    /// <param name="trackedBranchId">The identifier of the tracked branch.</param>
    /// <param name="commitSha">The commit SHA.</param>
    /// <param name="snapshotKind">The kind of snapshot.</param>
    /// <param name="baseSnapshotId">The optional base snapshot identifier.</param>
    public ProCursorIndexSnapshot(
        Guid id,
        Guid knowledgeSourceId,
        Guid trackedBranchId,
        string commitSha,
        string snapshotKind,
        Guid? baseSnapshotId = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must not be empty.", nameof(id));
        }

        if (knowledgeSourceId == Guid.Empty)
        {
            throw new ArgumentException("KnowledgeSourceId must not be empty.", nameof(knowledgeSourceId));
        }

        if (trackedBranchId == Guid.Empty)
        {
            throw new ArgumentException("TrackedBranchId must not be empty.", nameof(trackedBranchId));
        }

        this.Id = id;
        this.KnowledgeSourceId = knowledgeSourceId;
        this.TrackedBranchId = trackedBranchId;
        this.CommitSha = NormalizeRequired(commitSha, nameof(commitSha));
        this.SnapshotKind = NormalizeRequired(snapshotKind, nameof(snapshotKind));
        this.BaseSnapshotId = baseSnapshotId;
        this.Status = "building";
        this.CreatedAt = DateTimeOffset.UtcNow;
    }

    private ProCursorIndexSnapshot()
    {
    }

    /// <summary>
    ///     Gets the unique identifier for the snapshot.
    /// </summary>
    public Guid Id { get; private set; }

    /// <summary>
    ///     Gets the identifier of the knowledge source.
    /// </summary>
    public Guid KnowledgeSourceId { get; private set; }

    /// <summary>
    ///     Gets the identifier of the tracked branch.
    /// </summary>
    public Guid TrackedBranchId { get; private set; }

    /// <summary>
    ///     Gets the commit SHA.
    /// </summary>
    public string CommitSha { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the kind of snapshot.
    /// </summary>
    public string SnapshotKind { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the optional base snapshot identifier.
    /// </summary>
    public Guid? BaseSnapshotId { get; private set; }

    /// <summary>
    ///     Gets the status of the snapshot.
    /// </summary>
    public string Status { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets a value indicating whether the snapshot supports symbol queries.
    /// </summary>
    public bool SupportsSymbolQueries { get; private set; }

    /// <summary>
    ///     Gets the number of files in the snapshot.
    /// </summary>
    public int FileCount { get; private set; }

    /// <summary>
    ///     Gets the number of chunks in the snapshot.
    /// </summary>
    public int ChunkCount { get; private set; }

    /// <summary>
    ///     Gets the number of symbols in the snapshot.
    /// </summary>
    public int SymbolCount { get; private set; }

    /// <summary>
    ///     Gets the time when the snapshot was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    ///     Gets the time when the snapshot completed processing.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>
    ///     Gets the failure reason if the snapshot failed.
    /// </summary>
    public string? FailureReason { get; private set; }

    /// <summary>
    ///     Marks the snapshot as ready with the provided metadata.
    /// </summary>
    /// <param name="fileCount">The number of files in the snapshot.</param>
    /// <param name="chunkCount">The number of chunks in the snapshot.</param>
    /// <param name="symbolCount">The number of symbols in the snapshot.</param>
    /// <param name="supportsSymbolQueries">Whether the snapshot supports symbol queries.</param>
    public void MarkReady(int fileCount, int chunkCount, int symbolCount, bool supportsSymbolQueries)
    {
        if (!string.Equals(this.Status, "building", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Cannot complete a snapshot from status {this.Status}.");
        }

        if (fileCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileCount));
        }

        if (chunkCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkCount));
        }

        if (symbolCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(symbolCount));
        }

        this.Status = "ready";
        this.FileCount = fileCount;
        this.ChunkCount = chunkCount;
        this.SymbolCount = symbolCount;
        this.SupportsSymbolQueries = supportsSymbolQueries;
        this.CompletedAt = DateTimeOffset.UtcNow;
        this.FailureReason = null;
    }

    /// <summary>
    ///     Marks the snapshot as failed with the provided failure reason.
    /// </summary>
    /// <param name="failureReason">The reason why the snapshot failed.</param>
    public void MarkFailed(string failureReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);

        if (!string.Equals(this.Status, "building", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Cannot fail a snapshot from status {this.Status}.");
        }

        this.Status = "failed";
        this.CompletedAt = DateTimeOffset.UtcNow;
        this.FailureReason = failureReason.Trim();
    }

    /// <summary>
    ///     Marks the snapshot as superseded.
    /// </summary>
    public void MarkSuperseded()
    {
        if (!string.Equals(this.Status, "ready", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Cannot supersede a snapshot from status {this.Status}.");
        }

        this.Status = "superseded";
        this.CompletedAt ??= DateTimeOffset.UtcNow;
    }

    private static string NormalizeRequired(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value.Trim();
    }
}

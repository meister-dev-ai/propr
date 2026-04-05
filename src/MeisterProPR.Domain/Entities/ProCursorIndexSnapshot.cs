// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     Versioned persisted ProCursor snapshot for one source branch at one commit.
/// </summary>
public sealed class ProCursorIndexSnapshot
{
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

    public Guid Id { get; private set; }

    public Guid KnowledgeSourceId { get; private set; }

    public Guid TrackedBranchId { get; private set; }

    public string CommitSha { get; private set; } = string.Empty;

    public string SnapshotKind { get; private set; } = string.Empty;

    public Guid? BaseSnapshotId { get; private set; }

    public string Status { get; private set; } = string.Empty;

    public bool SupportsSymbolQueries { get; private set; }

    public int FileCount { get; private set; }

    public int ChunkCount { get; private set; }

    public int SymbolCount { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public string? FailureReason { get; private set; }

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

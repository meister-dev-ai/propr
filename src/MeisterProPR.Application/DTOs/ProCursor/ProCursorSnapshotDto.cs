// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs.ProCursor;

/// <summary>
///     ProCursor snapshot metadata exposed through application contracts.
/// </summary>
public sealed record ProCursorSnapshotDto(
    Guid Id,
    Guid KnowledgeSourceId,
    Guid TrackedBranchId,
    string BranchName,
    string CommitSha,
    string Status,
    bool SupportsSymbolQueries,
    int FileCount,
    int ChunkCount,
    int SymbolCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? FailureReason,
    string FreshnessStatus);

/// <summary>
///     Request used to enqueue manual indexing or refresh work.
/// </summary>
public sealed record ProCursorRefreshRequest(
    Guid? TrackedBranchId = null,
    string? RequestedCommitSha = null,
    string JobKind = "refresh");

/// <summary>
///     Durable job metadata returned when ProCursor refresh work is queued.
/// </summary>
public sealed record ProCursorIndexJobDto(
    Guid Id,
    Guid KnowledgeSourceId,
    Guid TrackedBranchId,
    string BranchName,
    string? RequestedCommitSha,
    string JobKind,
    ProCursorIndexJobStatus Status,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? FailureReason);

/// <summary>
///     Ephemeral materialized source state used by indexers and symbol extractors.
/// </summary>
public sealed record ProCursorMaterializedSource(
    Guid KnowledgeSourceId,
    Guid TrackedBranchId,
    string BranchName,
    string CommitSha,
    string RootDirectory,
    IReadOnlyList<string> MaterializedPaths);

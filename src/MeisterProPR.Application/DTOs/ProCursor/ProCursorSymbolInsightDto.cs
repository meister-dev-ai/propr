// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Application.DTOs.ProCursor;

/// <summary>
///     Request used to ask ProCursor for symbol-aware repository insight.
/// </summary>
public sealed record ProCursorSymbolQueryRequest(
    Guid ClientId,
    string Symbol,
    string QueryMode = "name",
    Guid? SourceId = null,
    string StateMode = "indexedSnapshot",
    ProCursorReviewContextDto? ReviewContext = null,
    int? MaxRelations = null);

/// <summary>
///     Review-scoped context used when a symbol query targets an overlay rather than a persisted snapshot.
/// </summary>
public sealed record ProCursorReviewContextDto(
    string RepositoryId,
    string SourceBranch,
    int PullRequestId,
    int IterationId);

/// <summary>
///     Symbol-aware response exposed by the ProCursor facade.
/// </summary>
public sealed record ProCursorSymbolInsightDto(
    string Status,
    Guid? SnapshotId,
    bool OverlayUsed,
    bool SupportsSymbolQueries,
    ProCursorSymbolMatchDto? Symbol,
    IReadOnlyList<ProCursorSymbolRelationDto> Relations,
    string? FreshnessStatus = null);

/// <summary>
///     Resolved symbol metadata.
/// </summary>
public sealed record ProCursorSymbolMatchDto(
    string SymbolKey,
    string DisplayName,
    string SymbolKind,
    string Language,
    string Signature,
    ProCursorSourceLocationDto Definition);

/// <summary>
///     One source location reported by a symbol query.
/// </summary>
public sealed record ProCursorSourceLocationDto(
    string FilePath,
    int LineStart,
    int LineEnd);

/// <summary>
///     One relationship edge returned with a symbol query.
/// </summary>
public sealed record ProCursorSymbolRelationDto(
    string RelationKind,
    string? FromSymbol,
    string? ToSymbol,
    string FilePath,
    int? LineStart,
    int? LineEnd);

/// <summary>
///     Result emitted by a ProCursor symbol extractor during indexing.
/// </summary>
public sealed record ProCursorSymbolExtractionResult(
    IReadOnlyList<ProCursorSymbolRecord> Symbols,
    IReadOnlyList<ProCursorSymbolEdge> Edges,
    bool SupportsSymbolQueries,
    string? UnsupportedReason = null);

/// <summary>
///     Ephemeral review-target symbol overlay built for one PR review context.
/// </summary>
public sealed record ProCursorMiniIndexOverlay(
    Guid OverlayId,
    Guid? BaseSnapshotId,
    bool SupportsSymbolQueries,
    IReadOnlyList<ProCursorSymbolRecord> Symbols,
    IReadOnlyList<ProCursorSymbolEdge> Edges,
    string FreshnessStatus,
    DateTimeOffset BuiltAt,
    DateTimeOffset ExpiresAt);

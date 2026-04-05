// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>
///     Structured symbol-insight result for a ProCursor symbol query.
/// </summary>
public sealed record ProCursorSymbolInsight(
    string Status,
    Guid? SnapshotId,
    bool OverlayUsed,
    bool SupportsSymbolQueries,
    ProCursorSymbolMetadata? Symbol,
    IReadOnlyList<ProCursorSymbolRelation> Relations,
    string? FreshnessStatus = null);

/// <summary>
///     Metadata about the resolved symbol.
/// </summary>
public sealed record ProCursorSymbolMetadata(
    string SymbolKey,
    string DisplayName,
    string SymbolKind,
    string Language,
    string Signature,
    ProCursorSymbolLocation Definition);

/// <summary>
///     One source location used in symbol results.
/// </summary>
public sealed record ProCursorSymbolLocation(
    string FilePath,
    int LineStart,
    int LineEnd);

/// <summary>
///     One relationship edge returned with symbol insight.
/// </summary>
public sealed record ProCursorSymbolRelation(
    string RelationKind,
    string? FromSymbol,
    string? ToSymbol,
    string FilePath,
    int? LineStart,
    int? LineEnd);

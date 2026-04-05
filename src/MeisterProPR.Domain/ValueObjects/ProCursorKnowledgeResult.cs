// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>
///     Structured result for a ProCursor knowledge query.
/// </summary>
public sealed record ProCursorKnowledgeResult(
    string Status,
    IReadOnlyList<ProCursorKnowledgeMatch> Results,
    string? NoResultReason = null);

/// <summary>
///     One sourced knowledge match returned from a ProCursor query.
/// </summary>
public sealed record ProCursorKnowledgeMatch(
    Guid SourceId,
    ProCursorSourceKind SourceKind,
    Guid SnapshotId,
    string Branch,
    string CommitSha,
    string ContentPath,
    string? Title,
    string Excerpt,
    string MatchKind,
    double Score,
    string FreshnessStatus);

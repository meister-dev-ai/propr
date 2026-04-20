// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs.ProCursor;

/// <summary>
///     Request used to ask a reviewer-facing ProCursor knowledge question.
/// </summary>
public sealed record ProCursorKnowledgeQueryRequest(
    Guid ClientId,
    string Question,
    IReadOnlyList<Guid>? KnowledgeSourceIds = null,
    ProCursorRepositoryContextDto? RepositoryContext = null,
    int? MaxResults = null);

/// <summary>
///     Optional repository context used to bias a knowledge query.
/// </summary>
public sealed record ProCursorRepositoryContextDto(
    string ProviderScopePath,
    string ProviderProjectKey,
    string RepositoryId,
    string Branch);

/// <summary>
///     Response returned from a ProCursor knowledge query.
/// </summary>
public sealed record ProCursorKnowledgeAnswerDto(
    string Status,
    IReadOnlyList<ProCursorKnowledgeAnswerMatchDto> Results,
    string? NoResultReason = null);

/// <summary>
///     One sourced match returned from a ProCursor knowledge query.
/// </summary>
public sealed record ProCursorKnowledgeAnswerMatchDto(
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

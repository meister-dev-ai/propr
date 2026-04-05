// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs.ProCursor;

/// <summary>
///     One safe ProCursor token usage event returned for recent-events drill-down.
/// </summary>
public sealed record ProCursorTokenUsageEventDto(
    DateTimeOffset OccurredAtUtc,
    string RequestId,
    ProCursorTokenUsageCallType CallType,
    string ModelName,
    string DeploymentName,
    long PromptTokens,
    long CompletionTokens,
    long TotalTokens,
    decimal? EstimatedCostUsd,
    bool TokensEstimated,
    bool CostEstimated,
    Guid? IndexJobId,
    string? SourcePath,
    string? ResourceId,
    Guid? KnowledgeChunkId);

/// <summary>
///     Envelope for recent ProCursor usage events for one knowledge source.
/// </summary>
public sealed record ProCursorTokenUsageEventsResponse(
    Guid ClientId,
    Guid SourceId,
    IReadOnlyList<ProCursorTokenUsageEventDto> Items);

/// <summary>
///     Flat export row used to render CSV output for ProCursor token usage.
/// </summary>
public sealed record ProCursorTokenUsageExportRowDto(
    DateOnly Date,
    Guid? SourceId,
    string SourceDisplayName,
    string ModelName,
    ProCursorTokenUsageCallType CallType,
    long PromptTokens,
    long CompletionTokens,
    long TotalTokens,
    decimal? EstimatedCostUsd,
    bool TokensEstimated,
    Guid? IndexJobId,
    string? SourcePath,
    string? ResourceId,
    Guid? KnowledgeChunkId);

/// <summary>
///     Request payload for rebuilding a selected ProCursor rollup interval.
/// </summary>
public sealed record ProCursorTokenUsageRebuildRequest(
    DateOnly From,
    DateOnly To,
    bool IncludeMonthly = true);

/// <summary>
///     Response returned after a ProCursor rollup rebuild request is accepted or completed.
/// </summary>
public sealed record ProCursorTokenUsageRebuildResponse(
    DateOnly From,
    DateOnly To,
    int RecomputedBucketCount,
    DateTimeOffset RebuiltAtUtc);

/// <summary>
///     Freshness metadata for the ProCursor usage rollup pipeline.
/// </summary>
public sealed record ProCursorTokenUsageFreshnessResponse(
    Guid ClientId,
    DateTimeOffset? LastRollupCompletedAtUtc,
    DateTimeOffset? LastRetentionRunAtUtc,
    bool IncludesEventGapFill);

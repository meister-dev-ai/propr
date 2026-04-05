// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs.ProCursor;

/// <summary>
///     Totals returned by ProCursor token usage endpoints.
/// </summary>
public sealed record ProCursorTokenUsageTotalsDto(
    long PromptTokens,
    long CompletionTokens,
    long TotalTokens,
    decimal? EstimatedCostUsd,
    long EventCount,
    long EstimatedEventCount);

/// <summary>
///     Breakdown item used in time-series and grouped reporting responses.
/// </summary>
public sealed record ProCursorTokenUsageBreakdownItemDto(
    Guid? SourceId,
    string? SourceDisplayName,
    string ModelName,
    long PromptTokens,
    long CompletionTokens,
    long TotalTokens,
    decimal? EstimatedCostUsd,
    bool Estimated,
    long EventCount = 0,
    long EstimatedEventCount = 0);

/// <summary>
///     One time bucket returned by a client-wide ProCursor usage query.
/// </summary>
public sealed record ProCursorTokenUsageSeriesPointDto(
    DateOnly BucketStart,
    long PromptTokens,
    long CompletionTokens,
    long TotalTokens,
    decimal? EstimatedCostUsd,
    IReadOnlyList<ProCursorTokenUsageBreakdownItemDto> Breakdown);

/// <summary>
///     Ranked top source item returned for a client-wide ProCursor usage query.
/// </summary>
public sealed record ProCursorTopSourceUsageDto(
    int Rank,
    Guid? SourceId,
    string SourceDisplayName,
    long TotalTokens,
    decimal? EstimatedCostUsd,
    long EstimatedEventCount);

/// <summary>
///     Ranked top-sources response returned for a selected client reporting period.
/// </summary>
public sealed record ProCursorTopSourcesResponse(
    Guid ClientId,
    string Period,
    IReadOnlyList<ProCursorTopSourceUsageDto> Items);

/// <summary>
///     Client-wide ProCursor token usage response.
/// </summary>
public sealed record ProCursorTokenUsageResponse(
    Guid ClientId,
    DateOnly From,
    DateOnly To,
    ProCursorTokenUsageGranularity Granularity,
    string? GroupBy,
    ProCursorTokenUsageTotalsDto Totals,
    IReadOnlyList<ProCursorTokenUsageSeriesPointDto> Series,
    IReadOnlyList<ProCursorTopSourceUsageDto> TopSources,
    bool IncludesGapFilledEvents = false,
    bool IncludesEstimatedUsage = false,
    DateTimeOffset? LastRollupCompletedAtUtc = null);

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs.ProCursor;

/// <summary>
///     Model-level breakdown entry for one ProCursor knowledge source.
/// </summary>
public sealed record ProCursorSourceModelUsageDto(
    string ModelName,
    long PromptTokens,
    long CompletionTokens,
    long TotalTokens,
    decimal? EstimatedCostUsd,
    long EventCount,
    long EstimatedEventCount);

/// <summary>
///     Source-level ProCursor token usage response.
/// </summary>
public sealed record ProCursorSourceTokenUsageResponse(
    Guid ClientId,
    Guid SourceId,
    string SourceDisplayName,
    DateOnly From,
    DateOnly To,
    ProCursorTokenUsageGranularity Granularity,
    ProCursorTokenUsageTotalsDto Totals,
    IReadOnlyList<ProCursorSourceModelUsageDto> ByModel,
    IReadOnlyList<ProCursorTokenUsageSeriesPointDto> Series,
    string? RecentEventsHref = null,
    bool IncludesGapFilledEvents = false,
    bool IncludesEstimatedUsage = false,
    DateTimeOffset? LastRollupCompletedAtUtc = null);

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>
///     Represents the token cost contribution of a single effort-tier / model-ID combination within a review job.
///     Stored as a JSONB array in the <c>review_jobs.token_breakdown</c> column. The cache and reasoning fields are
///     additive: rows written before they existed deserialize with each new field at zero.
/// </summary>
/// <param name="ConnectionCategory">The AI connection category (effort tier) that generated these tokens.</param>
/// <param name="ModelId">The effective AI model deployment name (e.g. "gpt-4o", "gpt-4o-mini").</param>
/// <param name="TotalInputTokens">Accumulated input tokens for this tier/model combination (includes the cached portion).</param>
/// <param name="TotalOutputTokens">Accumulated output tokens for this tier/model combination (includes the reasoning portion).</param>
/// <param name="TotalCachedInputTokens">Accumulated input tokens served from the provider prompt cache.</param>
/// <param name="TotalCacheWriteTokens">Accumulated tokens written to the provider prompt cache; zero for providers without a separate cache-write charge.</param>
/// <param name="TotalReasoningTokens">Accumulated reasoning tokens (a portion of <paramref name="TotalOutputTokens" />).</param>
/// <param name="EstimatedCostUsd">Estimated USD cost for this tier/model computed from its cumulative token totals; <see langword="null" /> when the model has no configured pricing.</param>
/// <param name="CostIsApproximate">True when <paramref name="EstimatedCostUsd" /> rests on a fallback rate, a missing rate, or estimated token counts.</param>
/// <param name="LogicalModelName">The logical-model role that produced these tokens, captured at the time; <see langword="null" /> for raw-model / non-logical-model passes (and rows written before this dimension existed).</param>
public sealed record TokenBreakdownEntry(
    AiConnectionModelCategory ConnectionCategory,
    string ModelId,
    long TotalInputTokens,
    long TotalOutputTokens,
    long TotalCachedInputTokens = 0,
    long TotalCacheWriteTokens = 0,
    long TotalReasoningTokens = 0,
    decimal? EstimatedCostUsd = null,
    bool CostIsApproximate = false,
    string? LogicalModelName = null);

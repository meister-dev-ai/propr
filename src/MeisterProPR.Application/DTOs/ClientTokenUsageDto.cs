// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.DTOs;

/// <summary>
///     A single (model, day) token usage data point returned by the token-usage dashboard endpoint.
/// </summary>
/// <param name="ModelId">The AI model identifier.</param>
/// <param name="Date">The UTC date the tokens were consumed.</param>
/// <param name="InputTokens">Total input tokens (includes the cached portion).</param>
/// <param name="OutputTokens">Total output tokens (includes the reasoning portion).</param>
/// <param name="CachedInputTokens">Cache-read input tokens.</param>
/// <param name="CacheWriteTokens">Cache-write tokens.</param>
/// <param name="ReasoningTokens">Reasoning tokens.</param>
/// <param name="EstimatedCostUsd">Accumulated estimated USD cost for this (model, day); null when no priced contribution was recorded.</param>
public sealed record ClientTokenUsageSampleDto(
    string ModelId,
    DateOnly Date,
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens = 0,
    long CacheWriteTokens = 0,
    long ReasoningTokens = 0,
    decimal? EstimatedCostUsd = null);

/// <summary>
///     Response DTO for <c>GET /admin/clients/{clientId}/token-usage</c>.
/// </summary>
/// <param name="ClientId">The client the usage belongs to.</param>
/// <param name="From">Inclusive start of the reported date range.</param>
/// <param name="To">Inclusive end of the reported date range.</param>
/// <param name="TotalInputTokens">Sum of input tokens across all samples (includes the cached portion).</param>
/// <param name="TotalOutputTokens">Sum of output tokens across all samples (includes the reasoning portion).</param>
/// <param name="Samples">The per-(model, day) usage samples.</param>
/// <param name="TotalCachedInputTokens">Sum of cache-read input tokens across all samples.</param>
/// <param name="TotalCacheWriteTokens">Sum of cache-write tokens across all samples.</param>
/// <param name="TotalReasoningTokens">Sum of reasoning tokens across all samples.</param>
/// <param name="TotalEstimatedCostUsd">Sum of estimated USD cost across all samples; null when no sample has a priced cost.</param>
public sealed record ClientTokenUsageDto(
    Guid ClientId,
    DateOnly From,
    DateOnly To,
    long TotalInputTokens,
    long TotalOutputTokens,
    IReadOnlyList<ClientTokenUsageSampleDto> Samples,
    long TotalCachedInputTokens = 0,
    long TotalCacheWriteTokens = 0,
    long TotalReasoningTokens = 0,
    decimal? TotalEstimatedCostUsd = null);

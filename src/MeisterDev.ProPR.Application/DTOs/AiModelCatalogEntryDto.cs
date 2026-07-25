// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>
///     A catalog entry as it applies to one client, after the scope layers have been resolved.
/// </summary>
/// <param name="ProviderId">Catalog-source identifier for the provider offering the model.</param>
/// <param name="ProviderName">Human-readable provider name.</param>
/// <param name="RemoteModelId">Model identifier as the provider knows it.</param>
/// <param name="DisplayName">Human-readable model name.</param>
/// <param name="Family">Model family, when known.</param>
/// <param name="SupportsToolUse">Whether the model supports tool or function calling.</param>
/// <param name="SupportsStructuredOutput">Whether the model accepts a response schema.</param>
/// <param name="SupportsReasoning">Whether the model performs, and bills for, reasoning.</param>
/// <param name="SupportsPromptCaching">Whether the model can serve part of a prompt from the provider cache.</param>
/// <param name="ReasoningContentField">Field the model needs echoed back to preserve its chain of thought; null when it has none.</param>
/// <param name="MaxContextTokens">Total context window in tokens.</param>
/// <param name="MaxOutputTokens">Maximum output tokens.</param>
/// <param name="InputCostPer1MUsd">Effective USD per million input tokens.</param>
/// <param name="OutputCostPer1MUsd">Effective USD per million output tokens.</param>
/// <param name="CachedInputCostPer1MUsd">Effective USD per million cache-read input tokens.</param>
/// <param name="CacheWriteCostPer1MUsd">Effective USD per million cache-write tokens.</param>
/// <param name="OpenWeights">Whether the model's weights are openly available.</param>
/// <param name="ReleaseDate">Release date, when known.</param>
/// <param name="PricingLayer">Which scope layer supplied the effective pricing, so the UI can show that a negotiated rate is in force.</param>
public sealed record AiModelCatalogEntryDto(
    string ProviderId,
    string ProviderName,
    string RemoteModelId,
    string DisplayName,
    string? Family,
    bool SupportsToolUse,
    bool SupportsStructuredOutput,
    bool SupportsReasoning,
    bool SupportsPromptCaching,
    string? ReasoningContentField,
    int? MaxContextTokens,
    int? MaxOutputTokens,
    decimal? InputCostPer1MUsd,
    decimal? OutputCostPer1MUsd,
    decimal? CachedInputCostPer1MUsd,
    decimal? CacheWriteCostPer1MUsd,
    bool OpenWeights,
    DateOnly? ReleaseDate,
    AiModelCatalogLayer PricingLayer);

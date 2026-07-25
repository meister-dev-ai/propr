// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>
///     A model an operator defines themselves, for something the snapshot does not describe: a private
///     fine-tune, a release newer than the bundled catalog, or a self-hosted model.
/// </summary>
/// <remarks>
///     Distinct from <see cref="AiModelCatalogOverrideDto" /> on purpose. An override adjusts the price of a model
///     the snapshot already describes and cannot restate its capabilities, because those are facts about the
///     model. A definition has no snapshot entry behind it, so it must supply its own facts — which is also why
///     defining a model the catalog already knows is refused rather than silently ignored.
/// </remarks>
/// <param name="ProviderId">Catalog provider the model is reached through.</param>
/// <param name="RemoteModelId">Model identifier as the provider knows it.</param>
/// <param name="DisplayName">Human-readable name; falls back to the identifier when empty.</param>
/// <param name="Family">Optional family label.</param>
/// <param name="SupportsToolUse">Whether the model supports tool or function calling.</param>
/// <param name="SupportsStructuredOutput">Whether the model accepts a response schema.</param>
/// <param name="SupportsReasoning">Whether the model performs, and bills for, reasoning.</param>
/// <param name="SupportsPromptCaching">Whether the model can serve part of a prompt from a provider cache.</param>
/// <param name="ReasoningContentField">Field the model needs echoed back to preserve its chain of thought, when it has such a requirement.</param>
/// <param name="MaxContextTokens">Total context window in tokens.</param>
/// <param name="MaxOutputTokens">Maximum output tokens.</param>
/// <param name="InputCostPer1MUsd">USD per million input tokens.</param>
/// <param name="OutputCostPer1MUsd">USD per million output tokens.</param>
/// <param name="CachedInputCostPer1MUsd">USD per million cache-read input tokens.</param>
/// <param name="CacheWriteCostPer1MUsd">USD per million cache-write tokens.</param>
public sealed record AiModelCatalogDefinitionDto(
    string ProviderId,
    string RemoteModelId,
    string? DisplayName = null,
    string? Family = null,
    bool SupportsToolUse = false,
    bool SupportsStructuredOutput = false,
    bool SupportsReasoning = false,
    bool SupportsPromptCaching = false,
    string? ReasoningContentField = null,
    int? MaxContextTokens = null,
    int? MaxOutputTokens = null,
    decimal? InputCostPer1MUsd = null,
    decimal? OutputCostPer1MUsd = null,
    decimal? CachedInputCostPer1MUsd = null,
    decimal? CacheWriteCostPer1MUsd = null);

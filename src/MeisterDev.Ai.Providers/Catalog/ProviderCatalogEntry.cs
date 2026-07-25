// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Catalog;

/// <summary>
///     One model as a catalog snapshot describes it. These are facts a provider or a public database can state
///     about a model, nothing more: what it can do, how large its context is, and what it costs per million
///     tokens. Which operations a host binds the model to, and where the entry came from, are the host's to
///     record, so they are deliberately absent — a snapshot source has no way to know either.
/// </summary>
/// <param name="ProviderId">Snapshot-source identifier for the provider offering the model, for example <c>deepseek</c>.</param>
/// <param name="ProviderName">Human-readable provider name.</param>
/// <param name="RemoteModelId">Model identifier as the provider knows it.</param>
/// <param name="DisplayName">Human-readable model name.</param>
/// <param name="Family">Model family, when the source states one.</param>
/// <param name="SupportsToolUse">Whether the model supports tool or function calling.</param>
/// <param name="SupportsStructuredOutput">Whether the model accepts a response schema.</param>
/// <param name="SupportsReasoning">Whether the model performs, and bills for, reasoning.</param>
/// <param name="SupportsPromptCaching">
///     Whether the source states a cache-read or cache-write price, which is the only reliable signal that
///     caching is billable.
/// </param>
/// <param name="ReasoningContentField">
///     Name of the field a model requires to be echoed back on assistant turns to preserve its chain of thought
///     (DeepSeek-style <c>reasoning_content</c>), or <see langword="null" /> when the model has no such
///     requirement. This is the per-model quirk a normalizing stage acts on.
/// </param>
/// <param name="MaxContextTokens">Total context window in tokens.</param>
/// <param name="MaxOutputTokens">Maximum output tokens.</param>
/// <param name="InputCostPer1MUsd">USD per million input tokens.</param>
/// <param name="OutputCostPer1MUsd">USD per million output tokens.</param>
/// <param name="CachedInputCostPer1MUsd">USD per million input tokens served from the provider cache.</param>
/// <param name="CacheWriteCostPer1MUsd">USD per million tokens written to the provider cache.</param>
/// <param name="OpenWeights">Whether the model's weights are openly available, which is what makes it self-hostable.</param>
/// <param name="ReleaseDate">Release date as the source states it.</param>
public sealed record ProviderCatalogEntry(
    string ProviderId,
    string ProviderName,
    string RemoteModelId,
    string DisplayName,
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
    decimal? CacheWriteCostPer1MUsd = null,
    bool OpenWeights = false,
    DateOnly? ReleaseDate = null);

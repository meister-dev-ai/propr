// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>
///     One configured or discovered model that belongs to an AI connection profile.
/// </summary>
/// <param name="SupportsReasoningEffortNone">
///     Whether ProPR sends the reasoning effort <c>none</c> to this model. For a review pass, an evidence judge call
///     or a contradiction check with the effort None, ProPR sends <c>none</c> to a model with this flag and leaves
///     the effort out for any other model, which then uses its own default. An operator sets the flag when the
///     provider documents the effort <c>none</c> for the model, because some reasoning models refuse it.
/// </param>
public sealed record AiConfiguredModelDto(
    Guid Id,
    string RemoteModelId,
    string DisplayName,
    IReadOnlyList<AiOperationKind> OperationKinds,
    IReadOnlyList<string> SupportedProtocolModes,
    string? TokenizerName = null,
    int? MaxInputTokens = null,
    int? EmbeddingDimensions = null,
    bool SupportsStructuredOutput = false,
    bool SupportsToolUse = false,
    AiConfiguredModelSource Source = AiConfiguredModelSource.Manual,
    DateTimeOffset? LastSeenAt = null,
    decimal? InputCostPer1MUsd = null,
    decimal? OutputCostPer1MUsd = null,
    int? MaxContextTokens = null,
    decimal? CachedInputCostPer1MUsd = null,
    decimal? CacheWriteCostPer1MUsd = null,
    bool SupportsReasoning = false,
    bool SupportsPromptCaching = false,
    string? ReasoningContentField = null,
    bool SupportsReasoningEffortNone = false)
{
    /// <summary>Returns <see langword="true" /> when the model supports chat workloads.</summary>
    public bool SupportsChat => this.OperationKinds.Contains(AiOperationKind.Chat);

    /// <summary>Returns <see langword="true" /> when the model supports embeddings.</summary>
    public bool SupportsEmbedding => this.OperationKinds.Contains(AiOperationKind.Embedding);
}

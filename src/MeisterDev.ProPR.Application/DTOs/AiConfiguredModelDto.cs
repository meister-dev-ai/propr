// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>
///     One configured or discovered model that belongs to an AI connection profile.
/// </summary>
/// <param name="DefaultReasoningLevel">
///     The level ProPR sends to this model for a call that no other place chooses a level for, or null to leave the
///     choice to the provider's own default. It is one of <paramref name="ReasoningLevels" />.
/// </param>
/// <param name="ReasoningLevels">
///     The reasoning levels the model accepts, by the provider's names and from the lowest to the highest effort. An
///     empty list means that the model accepts no level; null means that no source knows the levels yet.
/// </param>
/// <param name="ReasoningLevelsSource">Where <paramref name="ReasoningLevels" /> come from, or null when no source knows them.</param>
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
    string? DefaultReasoningLevel = null,
    IReadOnlyList<string>? ReasoningLevels = null,
    AiReasoningLevelSource? ReasoningLevelsSource = null)
{
    /// <summary>Returns <see langword="true" /> when the model supports chat workloads.</summary>
    public bool SupportsChat => this.OperationKinds.Contains(AiOperationKind.Chat);

    /// <summary>Returns <see langword="true" /> when the model supports embeddings.</summary>
    public bool SupportsEmbedding => this.OperationKinds.Contains(AiOperationKind.Embedding);
}

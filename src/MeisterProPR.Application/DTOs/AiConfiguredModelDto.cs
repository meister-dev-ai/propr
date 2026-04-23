// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs;

/// <summary>
///     One configured or discovered model that belongs to an AI connection profile.
/// </summary>
public sealed record AiConfiguredModelDto(
    Guid Id,
    string RemoteModelId,
    string DisplayName,
    IReadOnlyList<AiOperationKind> OperationKinds,
    IReadOnlyList<AiProtocolMode> SupportedProtocolModes,
    string? TokenizerName = null,
    int? MaxInputTokens = null,
    int? EmbeddingDimensions = null,
    bool SupportsStructuredOutput = false,
    bool SupportsToolUse = false,
    AiConfiguredModelSource Source = AiConfiguredModelSource.Manual,
    DateTimeOffset? LastSeenAt = null,
    decimal? InputCostPer1MUsd = null,
    decimal? OutputCostPer1MUsd = null)
{
    /// <summary>Returns <see langword="true" /> when the model supports chat workloads.</summary>
    public bool SupportsChat => this.OperationKinds.Contains(AiOperationKind.Chat);

    /// <summary>Returns <see langword="true" /> when the model supports embeddings.</summary>
    public bool SupportsEmbedding => this.OperationKinds.Contains(AiOperationKind.Embedding);
}

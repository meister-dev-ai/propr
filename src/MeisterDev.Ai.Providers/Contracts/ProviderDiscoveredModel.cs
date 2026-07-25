// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.Contracts;

/// <summary>
///     A model as the provider itself reports it during discovery. Carries only what the provider can be asked
///     about; where the entry came from and what it costs the host to use are the host's provenance to record.
/// </summary>
/// <param name="RemoteModelId">Model identifier as the provider knows it.</param>
/// <param name="DisplayName">Human-readable name, falling back to the remote identifier.</param>
/// <param name="OperationKinds">Operations the model can serve.</param>
/// <param name="SupportedProtocolModes">Protocol modes the model can serve.</param>
/// <param name="TokenizerName">Tokenizer the provider associates with the model, when known.</param>
/// <param name="MaxInputTokens">Maximum input tokens the provider reports, when known.</param>
/// <param name="MaxContextTokens">Maximum total context tokens the provider reports, when known.</param>
/// <param name="EmbeddingDimensions">Embedding dimensionality for an embedding model, when known.</param>
/// <param name="SupportsStructuredOutput">Whether the model accepts a response schema.</param>
/// <param name="SupportsToolUse">Whether the model supports tool or function calling.</param>
public sealed record ProviderDiscoveredModel(
    string RemoteModelId,
    string DisplayName,
    IReadOnlyList<AiOperationKind> OperationKinds,
    IReadOnlyList<AiProtocolMode> SupportedProtocolModes,
    string? TokenizerName = null,
    int? MaxInputTokens = null,
    int? MaxContextTokens = null,
    int? EmbeddingDimensions = null,
    bool SupportsStructuredOutput = false,
    bool SupportsToolUse = false);

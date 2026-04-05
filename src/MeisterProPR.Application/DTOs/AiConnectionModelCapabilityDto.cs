// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.DTOs;

/// <summary>
///     Per-deployment embedding capability metadata configured for an AI connection.
/// </summary>
/// <param name="ModelName">Deployment/model name as configured on the AI connection.</param>
/// <param name="TokenizerName">Tokenizer family used to count embedding input tokens.</param>
/// <param name="MaxInputTokens">Maximum total input tokens allowed per embedding request.</param>
/// <param name="EmbeddingDimensions">Embedding vector width returned by the deployment.</param>
/// <param name="InputCostPer1MUsd">Optional USD price per one million prompt/input tokens.</param>
/// <param name="OutputCostPer1MUsd">Optional USD price per one million completion/output tokens.</param>
public sealed record AiConnectionModelCapabilityDto(
    string ModelName,
    string TokenizerName,
    int MaxInputTokens,
    int EmbeddingDimensions,
    decimal? InputCostPer1MUsd = null,
    decimal? OutputCostPer1MUsd = null);

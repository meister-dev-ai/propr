// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>
///     EF Core persistence model for per-deployment embedding capability metadata under one AI connection.
/// </summary>
public sealed class AiConnectionModelCapabilityRecord
{
    public Guid Id { get; set; }

    public Guid AiConnectionId { get; set; }

    public string ModelName { get; set; } = string.Empty;

    public string TokenizerName { get; set; } = string.Empty;

    public int MaxInputTokens { get; set; }

    public int EmbeddingDimensions { get; set; }

    public decimal? InputCostPer1MUsd { get; set; }

    public decimal? OutputCostPer1MUsd { get; set; }

    public AiConnectionRecord? AiConnection { get; set; }
}

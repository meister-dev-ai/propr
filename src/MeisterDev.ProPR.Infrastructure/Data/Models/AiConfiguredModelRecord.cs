// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Data.Models;

/// <summary>
///     EF Core persistence model for one configured model under an AI connection profile.
/// </summary>
public sealed class AiConfiguredModelRecord
{
    public Guid Id { get; set; }

    public Guid ConnectionProfileId { get; set; }

    public string RemoteModelId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string[] OperationKinds { get; set; } = [];

    public string[] SupportedProtocolModes { get; set; } = [];

    public string? TokenizerName { get; set; }

    public int? MaxInputTokens { get; set; }

    public int? MaxContextTokens { get; set; }

    public int? EmbeddingDimensions { get; set; }

    public bool SupportsStructuredOutput { get; set; }

    public bool SupportsToolUse { get; set; }

    public string Source { get; set; } = string.Empty;

    public DateTimeOffset? LastSeenAt { get; set; }

    public decimal? InputCostPer1MUsd { get; set; }

    public decimal? OutputCostPer1MUsd { get; set; }

    public decimal? CachedInputCostPer1MUsd { get; set; }

    /// <summary>USD per million tokens written to the provider prompt cache; null when the provider does not bill cache creation.</summary>
    public decimal? CacheWriteCostPer1MUsd { get; set; }

    /// <summary>Whether the model performs, and bills for, reasoning.</summary>
    public bool SupportsReasoning { get; set; }

    /// <summary>Whether the model can serve part of a prompt from the provider cache.</summary>
    public bool SupportsPromptCaching { get; set; }

    /// <summary>
    ///     Field this model requires echoed back on assistant turns to preserve its chain of thought
    ///     (DeepSeek-style <c>reasoning_content</c>); null when it has no such requirement. Seeded from the
    ///     catalog so a normalizing stage is driven by data rather than a hard-coded model list.
    /// </summary>
    public string? ReasoningContentField { get; set; }

    public AiConnectionProfileRecord? ConnectionProfile { get; set; }

    public ICollection<AiPurposeBindingRecord> PurposeBindings { get; set; } = [];
}

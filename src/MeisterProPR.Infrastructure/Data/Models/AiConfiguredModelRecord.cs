// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Infrastructure.Data.Models;

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

    public int? EmbeddingDimensions { get; set; }

    public bool SupportsStructuredOutput { get; set; }

    public bool SupportsToolUse { get; set; }

    public string Source { get; set; } = string.Empty;

    public DateTimeOffset? LastSeenAt { get; set; }

    public decimal? InputCostPer1MUsd { get; set; }

    public decimal? OutputCostPer1MUsd { get; set; }

    public AiConnectionProfileRecord? ConnectionProfile { get; set; }

    public ICollection<AiPurposeBindingRecord> PurposeBindings { get; set; } = [];
}

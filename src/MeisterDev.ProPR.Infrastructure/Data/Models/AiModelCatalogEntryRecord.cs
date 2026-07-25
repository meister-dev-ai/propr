// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Data.Models;

/// <summary>
///     EF Core persistence model for one model-catalog entry. The catalog only ever seeds or suggests a
///     configured model; the configured-model row stays the authority at run time, so nothing here is read on a
///     model call.
/// </summary>
/// <remarks>
///     Scope is carried by the two nullable owner columns, and the combination distinguishes the three kinds of
///     row that share this table: both null is a global snapshot fact, a tenant id is that tenant's override
///     (above all its negotiated pricing), and a client id is that client's narrower override. Import touches
///     global rows only, which is what lets a refresh land without disturbing an override.
/// </remarks>
public sealed class AiModelCatalogEntryRecord
{
    public Guid Id { get; set; }

    /// <summary>Owning tenant for a tenant-scoped override; null for a global snapshot row or a client override.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>Owning client for a client-scoped override; null for a global snapshot row or a tenant override.</summary>
    public Guid? ClientId { get; set; }

    /// <summary>Snapshot-source identifier for the provider offering the model, for example <c>deepseek</c>.</summary>
    public string ProviderId { get; set; } = string.Empty;

    public string ProviderName { get; set; } = string.Empty;

    public string RemoteModelId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? Family { get; set; }

    public bool SupportsToolUse { get; set; }

    public bool SupportsStructuredOutput { get; set; }

    public bool SupportsReasoning { get; set; }

    public bool SupportsPromptCaching { get; set; }

    /// <summary>
    ///     Field a model requires echoed back on assistant turns to preserve its chain of thought
    ///     (DeepSeek-style <c>reasoning_content</c>); null when it has no such requirement.
    /// </summary>
    public string? ReasoningContentField { get; set; }

    public int? MaxContextTokens { get; set; }

    public int? MaxOutputTokens { get; set; }

    public decimal? InputCostPer1MUsd { get; set; }

    public decimal? OutputCostPer1MUsd { get; set; }

    public decimal? CachedInputCostPer1MUsd { get; set; }

    public decimal? CacheWriteCostPer1MUsd { get; set; }

    public bool OpenWeights { get; set; }

    public DateOnly? ReleaseDate { get; set; }

    /// <summary>Which snapshot format produced this row, kept for provenance when a refresh is diagnosed.</summary>
    public string SourceFormat { get; set; } = string.Empty;

    public DateTimeOffset ImportedAt { get; set; }
}

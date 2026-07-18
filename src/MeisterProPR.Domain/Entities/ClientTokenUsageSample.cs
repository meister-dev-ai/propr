// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     Represents a daily aggregate of token consumption for a single AI model, scoped to a client.
///     One row per (ClientId, ModelId, Date) — updated via upsert on each job completion.
/// </summary>
public sealed class ClientTokenUsageSample
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The client this usage belongs to.</summary>
    public Guid ClientId { get; set; }

    /// <summary>The AI model identifier (e.g. "gpt-4o", "text-embedding-3-small").</summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>The UTC date on which tokens were consumed.</summary>
    public DateOnly Date { get; set; }

    /// <summary>Total input/prompt tokens consumed on this date by this model for this client (includes the cached portion).</summary>
    public long InputTokens { get; set; }

    /// <summary>Total output/completion tokens consumed on this date by this model for this client (includes the reasoning portion).</summary>
    public long OutputTokens { get; set; }

    /// <summary>Total cache-read input tokens consumed on this date by this model for this client.</summary>
    public long CachedInputTokens { get; set; }

    /// <summary>Total cache-write tokens consumed on this date by this model for this client.</summary>
    public long CacheWriteTokens { get; set; }

    /// <summary>Total reasoning tokens consumed on this date by this model for this client.</summary>
    public long ReasoningTokens { get; set; }
}

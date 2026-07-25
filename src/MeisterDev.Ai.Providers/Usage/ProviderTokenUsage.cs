// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Usage;

/// <summary>
///     Normalized token-usage counts read from a single provider response, carrying the full breakdown a
///     provider usage payload can expose: input and output plus the cache-read, cache-write, and reasoning
///     portions. This is the library's own shape; a host maps it onto whatever its own accounting records use.
/// </summary>
/// <param name="InputTokens">Total prompt/input tokens the provider reported; already includes any cached-input tokens.</param>
/// <param name="OutputTokens">Total completion/output tokens the provider reported; includes reasoning tokens.</param>
/// <param name="CachedInputTokens">Portion of <see cref="InputTokens" /> served from the provider prompt cache.</param>
/// <param name="CacheWriteTokens">
///     Tokens written to the provider prompt cache (cache-creation); zero for providers without a separate
///     cache-write charge.
/// </param>
/// <param name="ReasoningTokens">Portion of <see cref="OutputTokens" /> spent on model reasoning.</param>
/// <param name="IsEstimated">True when the response carried no usage payload, so the counts are placeholder zeros rather than measured values.</param>
public sealed record ProviderTokenUsage(
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens = 0,
    long CacheWriteTokens = 0,
    long ReasoningTokens = 0,
    bool IsEstimated = false)
{
    /// <summary>An all-zero usage flagged as estimated, returned when a response reports no usage.</summary>
    public static ProviderTokenUsage Missing { get; } = new(0, 0, IsEstimated: true);

    /// <summary>An all-zero measured usage.</summary>
    public static ProviderTokenUsage Zero { get; } = new(0, 0);
}

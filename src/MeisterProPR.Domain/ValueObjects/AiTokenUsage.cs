// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>
///     Normalized token-usage counts extracted from a single AI response. Carries the full
///     breakdown a provider usage payload can expose — input and output plus the cache-read,
///     cache-write and reasoning portions — so review-side token stores can distinguish cached
///     from non-cached input and surface reasoning spend. Produced at the AI-layer boundary and
///     unpacked into the domain token records.
/// </summary>
/// <param name="InputTokens">Total prompt/input tokens the provider reported; already includes any cached-input tokens.</param>
/// <param name="OutputTokens">Total completion/output tokens the provider reported; includes reasoning tokens.</param>
/// <param name="CachedInputTokens">Portion of <see cref="InputTokens"/> served from the provider prompt cache.</param>
/// <param name="CacheWriteTokens">Tokens written to the provider prompt cache (cache-creation); zero for providers without a separate cache-write charge.</param>
/// <param name="ReasoningTokens">Portion of <see cref="OutputTokens"/> spent on model reasoning.</param>
/// <param name="IsEstimated">True when the response carried no usage payload, so the counts are placeholder zeros rather than measured values.</param>
public sealed record AiTokenUsage(
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens = 0,
    long CacheWriteTokens = 0,
    long ReasoningTokens = 0,
    bool IsEstimated = false)
{
    /// <summary>An all-zero usage flagged as estimated, returned when a response reports no usage.</summary>
    public static AiTokenUsage Missing { get; } = new(0, 0, IsEstimated: true);

    /// <summary>An all-zero measured usage.</summary>
    public static AiTokenUsage Zero { get; } = new(0, 0);

    /// <summary>
    ///     Input tokens billed at the non-cached rate: <see cref="InputTokens"/> minus the cached and
    ///     cache-write portions, floored at zero. The identity
    ///     <c>InputTokens == NonCachedInputTokens + CachedInputTokens + CacheWriteTokens</c> holds for
    ///     providers whose reported input total already includes the cached portion (the OpenAI family —
    ///     Azure/OpenAI/LiteLLM — where <see cref="CacheWriteTokens"/> is always zero). A future provider
    ///     that reports input <em>exclusive</em> of cache buckets (e.g. Anthropic) must be normalized in the
    ///     extractor before this identity applies; the floor keeps the value non-negative in the interim.
    /// </summary>
    public long NonCachedInputTokens => Math.Max(0, this.InputTokens - this.CachedInputTokens - this.CacheWriteTokens);
}

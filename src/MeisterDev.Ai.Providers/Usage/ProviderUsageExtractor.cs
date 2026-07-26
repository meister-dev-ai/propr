// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Enums;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Usage;

/// <summary>
///     Extracts a normalized <see cref="ProviderTokenUsage" /> from a Microsoft.Extensions.AI response.
/// </summary>
/// <remarks>
///     <para>
///         The client library's own <see cref="UsageDetails" /> properties are read first, because a provider
///         adapter that normalized a counter has already done this job better than a key lookup could. Where a
///         property is absent — cache-write has none at all, and reasoning is missing for providers whose
///         adapter does not map it — the counter is recovered from
///         <see cref="UsageDetails.AdditionalCounts" /> by name.
///     </para>
///     <para>
///         Those names are held per provider, which is the seam a new provider extends. Passing no provider kind
///         does not fall back to nothing: the union of every name any known provider uses is tried instead. That
///         matters because most callers extract usage without knowing which provider produced the response, and
///         a counter silently read as zero would understate a bill rather than fail visibly. The names are
///         provider-specific enough that the union cannot confuse two providers' counters.
///     </para>
/// </remarks>
public static class ProviderUsageExtractor
{
    /// <summary>
    ///     Counter names each provider family is known to report in <see cref="UsageDetails.AdditionalCounts" />.
    ///     A family with nothing to add leaves its set empty and is served by <see cref="AnyProviderKeys" />; a
    ///     new provider adds its own entry here rather than anywhere in the review loop.
    /// </summary>
    private static readonly IReadOnlyDictionary<AiProviderKind, UsageKeySet> KeysByProvider =
        new Dictionary<AiProviderKind, UsageKeySet>
        {
            [AiProviderKind.AzureOpenAi] = UsageKeySet.None,
            [AiProviderKind.OpenAi] = UsageKeySet.None,
            [AiProviderKind.LiteLlm] = UsageKeySet.None,
            [AiProviderKind.OpenAiCompatible] = UsageKeySet.None,

            // The AWS adapter maps the cache-read bucket onto the standard property but leaves the write bucket
            // under Bedrock's own name, which no other provider uses.
            [AiProviderKind.AwsBedrock] = new(
                CacheWrite: ["CacheWriteInputTokens"],
                CachedInput: ["CacheReadInputTokens"],
                Reasoning: []),

            // Anthropic names both cache buckets itself and reports no reasoning counter — its thinking tokens
            // are already inside the output count, so looking for one would only find another provider's name.
            [AiProviderKind.Anthropic] = new(
                CacheWrite: ["cache_creation_input_tokens"],
                CachedInput: ["cache_read_input_tokens"],
                Reasoning: []),
        };

    /// <summary>
    ///     Every counter name any known provider uses, tried when the provider is unknown or its own set does not
    ///     carry the counter being looked for.
    /// </summary>
    private static readonly UsageKeySet AnyProviderKeys = new(
        CacheWrite: ["cache_creation_input_tokens", "InputTokenDetails.CacheCreationTokenCount"],
        CachedInput: ["cached_tokens", "cache_read_input_tokens", "prompt_tokens_details.cached_tokens", "InputTokenDetails.CachedTokenCount"],
        Reasoning: ["reasoning_tokens", "completion_tokens_details.reasoning_tokens", "OutputTokenDetails.ReasoningTokenCount"]);

    /// <summary>
    ///     Builds a normalized usage record from a chat response. A response with no usage payload yields
    ///     <see cref="ProviderTokenUsage.Missing" /> (all-zero, flagged estimated) rather than a silent
    ///     measured zero.
    /// </summary>
    /// <param name="response">The AI chat response; may be <see langword="null" />.</param>
    /// <param name="providerKind">The provider family whose counter names to prefer; <see langword="null" /> tries every known name.</param>
    public static ProviderTokenUsage FromResponse(ChatResponse? response, AiProviderKind? providerKind = null)
        => FromUsage(response?.Usage, providerKind);

    /// <summary>
    ///     Builds a normalized usage record from a raw <see cref="UsageDetails" /> payload (chat or embedding).
    /// </summary>
    /// <param name="usage">The provider usage payload; may be <see langword="null" />.</param>
    /// <param name="providerKind">The provider family whose counter names to prefer; <see langword="null" /> tries every known name.</param>
    public static ProviderTokenUsage FromUsage(UsageDetails? usage, AiProviderKind? providerKind = null)
    {
        if (usage is null)
        {
            return ProviderTokenUsage.Missing;
        }

        var keys = providerKind is { } kind && KeysByProvider.TryGetValue(kind, out var mapped)
            ? mapped
            : UsageKeySet.None;

        var input = usage.InputTokenCount ?? 0;
        var output = usage.OutputTokenCount ?? 0;

        // A null property means the adapter did not map the counter, which is where the name lookup earns its
        // keep. A property that is present and zero is a measured zero and is left alone.
        var cachedInput = usage.CachedInputTokenCount
                          ?? ReadCount(usage, keys.CachedInput, AnyProviderKeys.CachedInput);
        var reasoning = usage.ReasoningTokenCount
                        ?? ReadCount(usage, keys.Reasoning, AnyProviderKeys.Reasoning);
        var cacheWrite = ReadCount(usage, keys.CacheWrite, AnyProviderKeys.CacheWrite);

        return new ProviderTokenUsage(input, output, cachedInput, cacheWrite, reasoning);
    }

    private static long ReadCount(UsageDetails usage, string[] preferred, string[] fallback)
    {
        var counts = usage.AdditionalCounts;
        if (counts is null || counts.Count == 0)
        {
            return 0;
        }

        foreach (var key in preferred)
        {
            if (counts.TryGetValue(key, out var preferredValue))
            {
                return preferredValue;
            }
        }

        foreach (var key in fallback)
        {
            if (counts.TryGetValue(key, out var value))
            {
                return value;
            }
        }

        return 0;
    }

    /// <summary>The counter names one provider family reports, by the counter each name carries.</summary>
    private sealed record UsageKeySet(string[] CacheWrite, string[] CachedInput, string[] Reasoning)
    {
        /// <summary>A family that reports nothing beyond what the client library already maps.</summary>
        public static UsageKeySet None { get; } = new([], [], []);
    }
}

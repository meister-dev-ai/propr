// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;

namespace MeisterProPR.Application.AI;

/// <summary>
///     Extracts a normalized <see cref="AiTokenUsage" /> from a Microsoft.Extensions.AI response.
///     Input, output, cache-read and reasoning counts are read from the native
///     <see cref="UsageDetails" /> properties — the OpenAI adapter populates
///     <see cref="UsageDetails.CachedInputTokenCount" /> and <see cref="UsageDetails.ReasoningTokenCount" />
///     on both the Chat-Completions and Responses API paths. Cache-write (cache-creation) tokens are
///     read from <see cref="UsageDetails.AdditionalCounts" /> via a per-provider key map; no current
///     OpenAI-family provider reports them, so the map is the extension seam for future providers
///     (for example Anthropic <c>cache_creation_input_tokens</c>).
/// </summary>
public static class AiTokenUsageExtractor
{
    /// <summary>
    ///     Per-provider <see cref="UsageDetails.AdditionalCounts" /> keys that carry cache-write tokens.
    ///     Empty for every current provider family (Azure/OpenAI/LiteLLM do not report cache-write);
    ///     populate an entry when a provider that bills cache creation is added.
    /// </summary>
    private static readonly IReadOnlyDictionary<AiProviderKind, string[]> CacheWriteKeysByProvider =
        new Dictionary<AiProviderKind, string[]>
        {
            [AiProviderKind.AzureOpenAi] = [],
            [AiProviderKind.OpenAi] = [],
            [AiProviderKind.LiteLlm] = [],
        };

    /// <summary>Cache-write keys tried when the provider kind is unknown or has no dedicated entry.</summary>
    private static readonly string[] DefaultCacheWriteKeys =
        ["cache_creation_input_tokens", "InputTokenDetails.CacheCreationTokenCount"];

    /// <summary>
    ///     Builds a normalized usage record from a chat response. A response with no usage payload
    ///     yields <see cref="AiTokenUsage.Missing" /> (all-zero, flagged estimated) rather than a
    ///     silent measured zero.
    /// </summary>
    /// <param name="response">The AI chat response; may be <see langword="null" />.</param>
    /// <param name="providerKind">The provider family used to pick cache-write keys; <see langword="null" /> selects the default keys.</param>
    public static AiTokenUsage FromResponse(ChatResponse? response, AiProviderKind? providerKind = null)
        => FromUsage(response?.Usage, providerKind);

    /// <summary>
    ///     Builds a normalized usage record from a raw <see cref="UsageDetails" /> payload (chat or embedding).
    /// </summary>
    /// <param name="usage">The provider usage payload; may be <see langword="null" />.</param>
    /// <param name="providerKind">The provider family used to pick cache-write keys; <see langword="null" /> selects the default keys.</param>
    public static AiTokenUsage FromUsage(UsageDetails? usage, AiProviderKind? providerKind = null)
    {
        if (usage is null)
        {
            return AiTokenUsage.Missing;
        }

        var input = usage.InputTokenCount ?? 0;
        var output = usage.OutputTokenCount ?? 0;
        var cachedInput = usage.CachedInputTokenCount ?? 0;
        var reasoning = usage.ReasoningTokenCount ?? 0;
        var cacheWrite = ReadCacheWriteTokens(usage, providerKind);

        return new AiTokenUsage(input, output, cachedInput, cacheWrite, reasoning);
    }

    private static long ReadCacheWriteTokens(UsageDetails usage, AiProviderKind? providerKind)
    {
        var counts = usage.AdditionalCounts;
        if (counts is null || counts.Count == 0)
        {
            return 0;
        }

        var keys = providerKind is { } kind
                   && CacheWriteKeysByProvider.TryGetValue(kind, out var mapped)
                   && mapped.Length > 0
            ? mapped
            : DefaultCacheWriteKeys;

        foreach (var key in keys)
        {
            if (counts.TryGetValue(key, out var value))
            {
                return value;
            }
        }

        return 0;
    }
}

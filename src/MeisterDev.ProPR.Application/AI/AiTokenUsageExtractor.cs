// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Usage;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Application.AI;

/// <summary>
///     Adapts the provider library's normalized usage shape onto the domain's <see cref="AiTokenUsage" />.
///     Reading a provider usage payload — including the per-provider cache-write key map — belongs to the
///     provider layer; this type exists so the review-side token stores keep receiving a domain value object
///     without taking a dependency on the library's shape.
/// </summary>
public static class AiTokenUsageExtractor
{
    /// <summary>
    ///     Builds a normalized usage record from a chat response. A response with no usage payload yields
    ///     <see cref="AiTokenUsage.Missing" /> (all-zero, flagged estimated) rather than a silent measured zero.
    /// </summary>
    /// <param name="response">The AI chat response; may be <see langword="null" />.</param>
    /// <param name="providerKind">The provider family used to pick cache-write keys; <see langword="null" /> selects the default keys.</param>
    public static AiTokenUsage FromResponse(ChatResponse? response, AiProviderKind? providerKind = null)
        => ToDomain(ProviderUsageExtractor.FromResponse(response, providerKind));

    /// <summary>
    ///     Builds a normalized usage record from a raw <see cref="UsageDetails" /> payload (chat or embedding).
    /// </summary>
    /// <param name="usage">The provider usage payload; may be <see langword="null" />.</param>
    /// <param name="providerKind">The provider family used to pick cache-write keys; <see langword="null" /> selects the default keys.</param>
    public static AiTokenUsage FromUsage(UsageDetails? usage, AiProviderKind? providerKind = null)
        => ToDomain(ProviderUsageExtractor.FromUsage(usage, providerKind));

    private static AiTokenUsage ToDomain(ProviderTokenUsage usage)
    {
        return new AiTokenUsage(
            usage.InputTokens,
            usage.OutputTokens,
            usage.CachedInputTokens,
            usage.CacheWriteTokens,
            usage.ReasoningTokens,
            usage.IsEstimated);
    }
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Usage;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Application.AI;

/// <summary>
///     Adapts the provider library's normalized usage shape onto the domain's <see cref="AiTokenUsage" />.
/// </summary>
/// <remarks>
///     The counters are read as the provider family's driver already mapped them: the runtime pipeline applies
///     that mapping to every response before it leaves the provider layer, and a relayed response carries the
///     counters the control plane's driver produced. Nothing here reads a vendor's field name, so a call site
///     needs no knowledge of which family served it.
/// </remarks>
public static class AiTokenUsageExtractor
{
    /// <summary>
    ///     Builds a normalized usage record from a chat response. A response with no usage payload yields
    ///     <see cref="AiTokenUsage.Missing" /> (all-zero, flagged estimated) rather than a silent measured zero.
    /// </summary>
    /// <param name="response">The AI chat response; may be <see langword="null" />.</param>
    public static AiTokenUsage FromResponse(ChatResponse? response)
        => ToDomain(ProviderTokenUsage.FromUsageDetails(response?.Usage));

    /// <summary>
    ///     Builds a normalized usage record from a raw <see cref="UsageDetails" /> payload (chat or embedding).
    /// </summary>
    /// <param name="usage">The provider usage payload; may be <see langword="null" />.</param>
    public static AiTokenUsage FromUsage(UsageDetails? usage)
        => ToDomain(ProviderTokenUsage.FromUsageDetails(usage));

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

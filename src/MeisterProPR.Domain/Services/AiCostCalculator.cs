// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Domain.Services;

/// <summary>
///     Pure conversion of a normalized token usage into a USD cost estimate given per-model pricing.
///     Non-cached input, cache-write and output tokens are billed at the input/output rates; cached input
///     is billed at the cached rate when configured, otherwise it falls back to the input rate. Output tokens
///     already include reasoning, so reasoning is billed at the output rate with no separate term. All
///     arithmetic is decimal with no rounding.
/// </summary>
public static class AiCostCalculator
{
    private const decimal TokensPerMillion = 1_000_000m;

    /// <summary>
    ///     Computes the USD cost estimate for <paramref name="usage" /> under <paramref name="pricing" />.
    /// </summary>
    /// <param name="usage">The normalized token usage to price.</param>
    /// <param name="pricing">The per-model USD pricing rates.</param>
    /// <returns>
    ///     An estimate whose cost is <see langword="null" /> when the model has neither an input nor an output
    ///     rate configured, and whose <see cref="AiCostEstimate.IsApproximate" /> flag is set when the estimate
    ///     rests on a fallback rate, a missing rate, or estimated token counts.
    /// </returns>
    public static AiCostEstimate Calculate(AiTokenUsage usage, ModelPricing pricing)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(pricing);

        if (pricing.InputCostPer1MUsd is null && pricing.OutputCostPer1MUsd is null)
        {
            return new AiCostEstimate(null, true);
        }

        var inputRate = pricing.InputCostPer1MUsd ?? 0m;
        var outputRate = pricing.OutputCostPer1MUsd ?? 0m;
        var cachedRate = pricing.CachedInputCostPer1MUsd ?? inputRate;

        var usd = (usage.NonCachedInputTokens * inputRate / TokensPerMillion)
                  + (usage.CachedInputTokens * cachedRate / TokensPerMillion)
                  + (usage.CacheWriteTokens * inputRate / TokensPerMillion)
                  + (usage.OutputTokens * outputRate / TokensPerMillion);

        var isApproximate = usage.IsEstimated
                            || (pricing.CachedInputCostPer1MUsd is null && usage.CachedInputTokens > 0)
                            || usage.CacheWriteTokens > 0
                            || (pricing.InputCostPer1MUsd is null && usage.NonCachedInputTokens + usage.CachedInputTokens > 0)
                            || (pricing.OutputCostPer1MUsd is null && usage.OutputTokens > 0);

        return new AiCostEstimate(usd, isApproximate);
    }
}

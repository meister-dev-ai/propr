// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>
///     Per-model USD pricing used to convert token counts into a monetary estimate. Rates are quoted per
///     one million tokens. A <see langword="null" /> rate means "pricing unknown" for that dimension and is
///     distinct from a configured rate of <c>0</c>.
/// </summary>
/// <param name="InputCostPer1MUsd">USD price per one million non-cached input tokens; <see langword="null" /> when unknown.</param>
/// <param name="OutputCostPer1MUsd">USD price per one million output tokens (output already includes reasoning); <see langword="null" /> when unknown.</param>
/// <param name="CachedInputCostPer1MUsd">USD price per one million cached (cache-read) input tokens; <see langword="null" /> to fall back to <paramref name="InputCostPer1MUsd" />.</param>
public sealed record ModelPricing(
    decimal? InputCostPer1MUsd,
    decimal? OutputCostPer1MUsd,
    decimal? CachedInputCostPer1MUsd = null);

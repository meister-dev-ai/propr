// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>
///     The USD cost estimate for a single token usage, with a flag marking values that rest on a pricing
///     fallback or missing rate. A <see langword="null" /> <paramref name="Usd" /> means the model had no
///     configured pricing at all, which is deliberately distinct from a measured cost of <c>0</c>.
/// </summary>
/// <param name="Usd">The estimated cost in USD, or <see langword="null" /> when the model has no configured pricing.</param>
/// <param name="IsApproximate">True when the estimate rests on a fallback rate, a missing rate, or estimated token counts.</param>
public readonly record struct AiCostEstimate(
    decimal? Usd,
    bool IsApproximate);

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>
///     The closed routing-tier algebra. The final tier is the commutative <see cref="Max" /> over a
///     fixed <see cref="FileComplexityTier" /> lattice (Low &lt; Medium &lt; High). Floors are absolute levels,
///     never increments — adding a floor can only raise the result, never lower it. Used to combine the
///     model-judged tier with escalate-only floors (blast-radius, security) on the deeper review pass.
/// </summary>
public static class TierJoin
{
    /// <summary>
    ///     The commutative, order-independent join: the highest tier among the operands (Low when none).
    /// </summary>
    public static FileComplexityTier Max(params FileComplexityTier[] tiers)
    {
        return tiers.DefaultIfEmpty(FileComplexityTier.Low).Max();
    }

    /// <summary>
    ///     The escalate-only floor contributed by the blast-radius signal: a <see cref="FanOutKind.Truncated" />
    ///     lookup (too many references to count — definitively high impact) floors at <see cref="FileComplexityTier.Medium" />.
    ///     Everything else (Measured, Unavailable) contributes no floor (<see cref="FileComplexityTier.Low" />),
    ///     leaving the result to the model-judged tier.
    /// </summary>
    public static FileComplexityTier FloorFromFanOut(FanOutSignal fanOut)
    {
        return fanOut.Kind == FanOutKind.Truncated ? FileComplexityTier.Medium : FileComplexityTier.Low;
    }
}

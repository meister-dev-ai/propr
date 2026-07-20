// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Budgeting.Models;

/// <summary>
///     Accumulated review spend within a single budget scope. <see cref="KnownUsd" /> sums only the priced
///     contributions; <see cref="IsApproximate" /> is set when some contribution had no known USD cost (an
///     unpriced model) or rests on an approximate estimate, so the true spend is at least <see cref="KnownUsd" />
///     but may be higher.
/// </summary>
/// <param name="KnownUsd">The summed USD cost of the priced contributions to this scope.</param>
/// <param name="IsApproximate">True when the scope total omits an unpriced contribution or rests on an approximate estimate.</param>
public sealed record ReviewScopeSpend(decimal KnownUsd, bool IsApproximate)
{
    /// <summary>An empty scope with no recorded spend.</summary>
    public static ReviewScopeSpend None { get; } = new(0m, false);
}

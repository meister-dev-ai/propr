// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Features.Budgeting.Models;

/// <summary>
///     Describes the budget cap that a review has reached: which scope and threshold bound the review, the
///     threshold value, and the spend that reached it. Surfaced to operators as the reason a review was held or
///     stopped.
/// </summary>
/// <param name="Scope">The budget scope whose cap was reached.</param>
/// <param name="CapKind">Whether the soft or the hard cap was reached.</param>
/// <param name="ThresholdUsd">The USD threshold that was reached.</param>
/// <param name="SpentUsd">The scope's accumulated USD spend that reached (or exceeded) the threshold.</param>
public sealed record BudgetBreach(
    BudgetScopeKind Scope,
    BudgetCapKind CapKind,
    decimal ThresholdUsd,
    decimal SpentUsd);

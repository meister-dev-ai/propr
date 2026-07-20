// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Budgeting.Models;

namespace MeisterProPR.Application.Features.Budgeting;

/// <summary>
///     Thrown when a review's accumulated spend reaches a hard cap and a further model call must not be made.
///     Derives from <see cref="OperationCanceledException" /> so it rides the cancellation channel that review
///     code paths already honor (and re-throw) rather than being swallowed by a broad fallback catch and
///     degrading silently. The orchestrator distinguishes it by type to publish partial findings and mark the job
///     budget-exceeded.
/// </summary>
public sealed class BudgetHardCapReachedException : OperationCanceledException
{
    /// <summary>Initializes a new instance of the <see cref="BudgetHardCapReachedException" /> class.</summary>
    /// <param name="breach">The hard cap that was reached.</param>
    public BudgetHardCapReachedException(BudgetBreach breach)
        : base(
            $"Review spend reached the {breach.Scope} hard cap of {breach.ThresholdUsd:0.######} USD (spent {breach.SpentUsd:0.######} USD); no further model call is made.")
    {
        this.Breach = breach;
    }

    /// <summary>The hard cap that was reached.</summary>
    public BudgetBreach Breach { get; }
}

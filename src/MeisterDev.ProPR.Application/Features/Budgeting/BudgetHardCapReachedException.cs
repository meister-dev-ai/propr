// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Budgeting.Models;

namespace MeisterDev.ProPR.Application.Features.Budgeting;

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
    /// <param name="breach">
    ///     The hard cap that was reached, where the caller knows which one it was. A refusal relayed from the
    ///     control plane names the condition without carrying the cap, and the job is still finalised as
    ///     budget-exceeded, so the detail is optional and the handler falls back to the scope it holds.
    /// </param>
    public BudgetHardCapReachedException(BudgetBreach? breach)
        : base(
            breach is null
                ? "Review spend reached a hard cap; no further model call is made."
                : $"Review spend reached the {breach.Scope} hard cap of {breach.ThresholdUsd:0.######} USD (spent {breach.SpentUsd:0.######} USD); no further model call is made.")
    {
        this.Breach = breach;
    }

    /// <summary>The hard cap that was reached, where the refusal named it.</summary>
    public BudgetBreach? Breach { get; }

    /// <summary>The cap this refusal is recorded against, or null when neither side names one.</summary>
    /// <remarks>
    ///     A refusal relayed from the control plane names the condition without carrying the cap, because the
    ///     cap belongs to the replica that refused and not to the caller holding this exception. The caller's
    ///     own scope names it where the caller has one. Where neither does, the cap stays unknown and the
    ///     caller records a terminal stop carrying no scope, threshold or spend. The outcome is terminal
    ///     either way: recording it as an ordinary failure would leave the work retryable, and every retry
    ///     meets the same cap and spends another attempt reaching it. Naming a cap that no side read would
    ///     put a threshold and a spend on the job that no configured cap has.
    /// </remarks>
    /// <param name="heldByTheCaller">The cap the caller's own budget scope tripped, where it tripped one.</param>
    public BudgetBreach? ResolveBreach(BudgetBreach? heldByTheCaller)
    {
        return this.Breach ?? heldByTheCaller;
    }
}

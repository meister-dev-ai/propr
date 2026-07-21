// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterProPR.Application.Features.Budgeting.Models;

namespace MeisterProPR.Application.Features.Budgeting;

/// <summary>
///     The ambient budget state for one in-flight review job: the client's caps, the per-scope spend baseline
///     captured at job start, and a thread-safe running total of the spend this job has metered since. The same
///     running total contributes to every scope (a job's spend counts toward its client, pull request, and
///     increment at once), so the effective spend for each scope is its baseline plus the running total.
/// </summary>
public sealed class BudgetScope(BudgetCaps caps, ReviewSpendBaseline baseline)
{
    private readonly object _gate = new();
    private decimal _runningUsd;
    private bool _runningApproximate;
    private BudgetBreach? _trippedBreach;
    private BudgetBreach? _incrementSoftCapBreach;

    /// <summary>The client's resolved caps.</summary>
    public BudgetCaps Caps { get; } = caps;

    /// <summary>The per-scope spend already accumulated before this job's in-run spend.</summary>
    public ReviewSpendBaseline Baseline { get; } = baseline;

    /// <summary>The USD spend this job has metered so far in the current run.</summary>
    public decimal RunningUsd
    {
        get
        {
            lock (this._gate)
            {
                return this._runningUsd;
            }
        }
    }

    /// <summary>True when some metered call had no known cost, so the running total is a lower bound.</summary>
    public bool RunningIsApproximate
    {
        get
        {
            lock (this._gate)
            {
                return this._runningApproximate;
            }
        }
    }

    /// <summary>
    ///     The hard cap that was reached, once one has been, or <see langword="null" />. Recorded before the
    ///     enforcing exception is thrown so the orchestrator can recognize a budget cut as such even if the
    ///     exception surfaces wrapped by an intervening layer.
    /// </summary>
    public BudgetBreach? TrippedBreach
    {
        get
        {
            lock (this._gate)
            {
                return this._trippedBreach;
            }
        }
    }

    /// <summary>
    ///     The per-increment soft cap once it has been reached during this run, or <see langword="null" />. Recorded
    ///     the first time <see cref="IsIncrementSoftCapReached" /> observes the cap so the orchestrator can complete
    ///     the review with a soft-capped note and marker without re-deriving the breach.
    /// </summary>
    public BudgetBreach? IncrementSoftCapBreach
    {
        get
        {
            lock (this._gate)
            {
                return this._incrementSoftCapBreach;
            }
        }
    }

    /// <summary>
    ///     Records the USD cost of a completed model call. A <see langword="null" /> cost (an unpriced model)
    ///     contributes nothing to the total but flags it approximate rather than being coerced to zero.
    /// </summary>
    /// <param name="costUsd">The call's USD cost, or <see langword="null" /> when the model has no known pricing.</param>
    public void RecordCall(decimal? costUsd)
    {
        lock (this._gate)
        {
            if (costUsd is { } cost)
            {
                this._runningUsd += cost;
            }
            else
            {
                this._runningApproximate = true;
            }
        }
    }

    /// <summary>
    ///     Throws <see cref="BudgetHardCapReachedException" /> when the effective spend in any scope has reached a
    ///     configured hard cap. Called before each model call so the call is not made once a cap is reached.
    /// </summary>
    public void ThrowIfHardCapReached()
    {
        if (!this.Caps.AnyHardCapConfigured)
        {
            return;
        }

        decimal running;
        lock (this._gate)
        {
            running = this._runningUsd;
        }

        var breach = BudgetEvaluator.FindHardCapBreach(
            this.Caps,
            this.Baseline.ClientMonthToDate.KnownUsd + running,
            this.Baseline.PullRequest.KnownUsd + running,
            this.Baseline.Increment.KnownUsd + running);

        if (breach is not null)
        {
            lock (this._gate)
            {
                this._trippedBreach ??= breach;
            }

            throw new BudgetHardCapReachedException(breach);
        }
    }

    /// <summary>
    ///     Returns whether the effective increment spend has reached the configured per-increment soft cap. Unlike
    ///     the hard cap this never throws: the per-file dispatch loop calls it between files so a running job stops
    ///     scanning further files once the cap is reached, then still concludes with a synthesis. The breach is
    ///     recorded the first time it is observed so the outcome can be surfaced as a soft-capped note and marker.
    /// </summary>
    public bool IsIncrementSoftCapReached()
    {
        if (!this.Caps.IncrementSoftCapConfigured)
        {
            return false;
        }

        decimal running;
        lock (this._gate)
        {
            running = this._runningUsd;
        }

        var breach = BudgetEvaluator.FindIncrementSoftCapBreach(
            this.Caps,
            this.Baseline.Increment.KnownUsd + running);

        if (breach is null)
        {
            return false;
        }

        lock (this._gate)
        {
            this._incrementSoftCapBreach ??= breach;
        }

        return true;
    }
}

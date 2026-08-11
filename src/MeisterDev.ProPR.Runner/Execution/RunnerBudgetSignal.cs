// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Runner.Execution;

/// <summary>
///     One job's word from the relay that its budget is spent.
///     <para>
///         The figures live in the control plane, where every completion is priced and capped, so what comes
///         back is a flag rather than a number: the soft cap has been reached, or a completion was refused
///         outright. The planner reads it before starting each file, which is the graceful version of what
///         otherwise happens anyway: every remaining file failing one 402 at a time.
///     </para>
/// </summary>
public sealed class RunnerBudgetSignal
{
    private volatile bool _exhausted;

    /// <summary>Whether the review should stop starting new scanning work.</summary>
    public bool Exhausted => this._exhausted;

    /// <summary>Latches the signal; there is no way back inside one job, exactly like the cap itself.</summary>
    public void MarkExhausted()
    {
        this._exhausted = true;
    }
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Budgeting;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Chat completions performed by the control plane on an executor's behalf.
///     <para>
///         Completions are the highest-volume call a review makes and the one whose credential is worth the
///         most, so the key never leaves the control plane: the executor names a logical model and the relay
///         resolves it against the stored connection.
///     </para>
///     <para>
///         Because every completion passes through one place, the hard cap becomes a chokepoint rather than
///         a reconciliation done after the money is spent. The budget for a job is held against the job
///         itself, not against whichever thread happens to be serving the call, which is what makes it hold
///         when the spender is in another process.
///     </para>
/// </summary>
public interface IRunnerAiRelay
{
    /// <summary>
    ///     Performs one completion for a leased job, or refuses it.
    /// </summary>
    /// <param name="call">The caller's job, lease generation, and identity.</param>
    /// <param name="request">Which logical model to use, the payload, and the idempotency key.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<RunnerRelayResult> CompleteAsync(
        RunnerCallContext call,
        RunnerRelayRequest request,
        CancellationToken ct = default);
}

/// <summary>
///     The per-job budget the relay charges against. Held per leased job rather than flowing on the call
///     context: a runner's spend arrives on whatever request thread the control plane happens to serve it
///     on, so an ambient scope would charge nothing and the cap would never trip.
/// </summary>
public interface IRunnerJobBudgetRegistry
{
    /// <summary>Holds the budget for a leased job.</summary>
    void Register(Guid jobId, BudgetScope scope);

    /// <summary>Returns the budget held for a job, or null when none is.</summary>
    BudgetScope? Find(Guid jobId);

    /// <summary>Drops the budget held for a job. Safe to call when none is held.</summary>
    void Release(Guid jobId);
}

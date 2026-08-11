// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Budgeting.Models;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     One completion an executor asks the control plane to perform for it.
/// </summary>
/// <param name="LogicalModelName">
///     The named model role to use. A name, never a connection: resolving the name against the stored
///     connection is what keeps the provider key on the control plane.
/// </param>
/// <param name="Messages">The conversation to complete.</param>
/// <param name="Options">
///     Chat options, including any tools the pass offers. Tool calling is not optional here: review passes
///     use it, and a relay that dropped it would turn a tool-using review into a different review.
/// </param>
/// <param name="IdempotencyKey">
///     Identifies this completion attempt. A retry carrying the same key is answered from what the first
///     attempt produced and charged nothing further, because the money was already spent.
/// </param>
public sealed record RunnerRelayRequest(
    string LogicalModelName,
    IReadOnlyList<ChatMessage> Messages,
    ChatOptions? Options,
    string IdempotencyKey);

/// <summary>Why a relayed completion was not performed.</summary>
public enum RunnerRelayRefusal
{
    /// <summary>The completion was performed.</summary>
    None = 0,

    /// <summary>The caller is not entitled to act on this job; see the call refusal for which reason.</summary>
    NotAuthorized = 1,

    /// <summary>
    ///     A hard budget cap is reached. The pipeline already knows this condition and winds the job down as
    ///     budget-exceeded rather than treating it as a failure.
    /// </summary>
    BudgetHardCapReached = 2,

    /// <summary>The control plane is not holding this job open, so it cannot charge or serve the call.</summary>
    JobNotHeld = 3,
}

/// <summary>The answer to a relayed completion.</summary>
/// <param name="Response">The model's response when the call was performed.</param>
/// <param name="Refusal">Why it was not performed, or <see cref="RunnerRelayRefusal.None" />.</param>
/// <param name="CallRefusal">Which authorization reason applied, when the refusal was an authorization one.</param>
/// <param name="Breach">The cap that was reached, when the refusal was a budget one.</param>
/// <param name="SoftCapReached">
///     Whether the per-increment soft cap has been reached. Readable rather than enforced: the soft cap
///     means wind down to a synthesis, not stop, so the executor needs to see it without being refused.
/// </param>
/// <param name="Replayed">True when this answer came from an earlier attempt carrying the same key.</param>
public sealed record RunnerRelayResult(
    ChatResponse? Response,
    RunnerRelayRefusal Refusal,
    RunnerCallRefusal CallRefusal,
    BudgetBreach? Breach,
    bool SoftCapReached,
    bool Replayed)
{
    /// <summary>Whether a response was produced.</summary>
    public bool IsCompleted => this.Refusal == RunnerRelayRefusal.None && this.Response is not null;

    /// <summary>A completion that was performed.</summary>
    public static RunnerRelayResult Completed(ChatResponse response, bool softCapReached, bool replayed = false)
    {
        return new RunnerRelayResult(response, RunnerRelayRefusal.None, RunnerCallRefusal.None, null, softCapReached, replayed);
    }

    /// <summary>A completion refused because the caller may not act on the job.</summary>
    public static RunnerRelayResult NotAuthorized(RunnerCallRefusal refusal)
    {
        return new RunnerRelayResult(null, RunnerRelayRefusal.NotAuthorized, refusal, null, false, false);
    }

    /// <summary>A completion refused because a hard cap is reached.</summary>
    public static RunnerRelayResult BudgetExceeded(BudgetBreach breach)
    {
        return new RunnerRelayResult(null, RunnerRelayRefusal.BudgetHardCapReached, RunnerCallRefusal.None, breach, false, false);
    }

    /// <summary>A completion refused because the control plane is not holding the job open.</summary>
    public static RunnerRelayResult JobNotHeld()
    {
        return new RunnerRelayResult(null, RunnerRelayRefusal.JobNotHeld, RunnerCallRefusal.None, null, false, false);
    }
}

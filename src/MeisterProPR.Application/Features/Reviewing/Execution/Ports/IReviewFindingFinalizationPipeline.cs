// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Runs the ordered finding-finalization checks after the deterministic gate. Each check may refine or
///     replace a finding's decision; the pipeline folds them per finding, records their observations to the job
///     protocol, and returns the final decision set. It never mutates the base gate's own logic — it composes on
///     top of it — so additional checks can be registered without changing the gate.
/// </summary>
public interface IReviewFindingFinalizationPipeline
{
    /// <summary>
    ///     Applies the registered checks to each finding's base gate decision.
    /// </summary>
    /// <param name="findings">The candidate findings being finalized.</param>
    /// <param name="baseDecisions">The decisions produced by the deterministic gate, one per finding.</param>
    /// <param name="protocolId">Optional job-protocol identifier for recording check observations.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The final decisions after all checks, one per finding in the input order.</returns>
    Task<IReadOnlyList<FinalGateDecision>> ApplyAsync(
        IReadOnlyList<CandidateReviewFinding> findings,
        IReadOnlyList<FinalGateDecision> baseDecisions,
        Guid? protocolId,
        CancellationToken ct = default);
}

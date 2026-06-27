// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Verifies extracted review claims and recommends the conservative final disposition.
/// </summary>
public interface IReviewFindingVerifier
{
    /// <summary>
    ///     Verifies extracted work items against bounded evidence and invariants.
    /// </summary>
    /// <param name="workItems">Verification work items to evaluate.</param>
    /// <param name="invariantFacts">Invariant facts that can block publication.</param>
    /// <param name="verificationContext">
    ///     Optional per-run context (review tools, source branch, chat client/model) enabling an
    ///     evidence-gathering verifier to substantiate a claim. <see langword="null" /> for
    ///     evidence-free verifiers or when the hosting path cannot supply it.
    /// </param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The verification outcomes for the supplied work items.</returns>
    Task<IReadOnlyList<VerificationOutcome>> VerifyAsync(
        IReadOnlyList<VerificationWorkItem> workItems,
        IReadOnlyList<InvariantFact> invariantFacts,
        ReviewVerificationContext? verificationContext = null,
        CancellationToken ct = default);
}

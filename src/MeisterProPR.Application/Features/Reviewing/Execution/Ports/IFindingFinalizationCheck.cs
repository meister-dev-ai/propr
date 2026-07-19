// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     A single, composable check in the post-gate finding-finalization pipeline. Each check receives a finding
///     and the decision made so far and returns the decision it wants in place — unchanged, refined (extra reason
///     code / publication note), or replaced (different disposition) — plus an optional observation to record.
///     Checks are deterministic and side-effect free; the pipeline owns ordering and protocol emission, so new
///     checks compose without editing existing ones.
/// </summary>
public interface IFindingFinalizationCheck
{
    /// <summary>Gets the stable name of the check, used in protocol observations.</summary>
    string Name { get; }

    /// <summary>
    ///     Evaluates the finding against the decision made by earlier stages and returns the decision this check
    ///     wants carried forward.
    /// </summary>
    /// <param name="finding">The candidate finding being finalized.</param>
    /// <param name="currentDecision">The decision produced by the base gate and any earlier checks.</param>
    /// <returns>The (possibly changed) decision and an optional observation to record.</returns>
    FinalizationCheckOutcome Evaluate(CandidateReviewFinding finding, FinalGateDecision currentDecision);
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Reviewing-owned boundary for deterministic post-synthesis finding gating.
/// </summary>
public interface IDeterministicReviewFindingGate
{
    /// <summary>
    ///     Evaluates final candidate findings and returns exactly one disposition per finding.
    /// </summary>
    Task<IReadOnlyList<FinalGateDecision>> EvaluateAsync(
        IReadOnlyList<CandidateReviewFinding> findings,
        IReadOnlyList<InvariantFact> invariantFacts,
        CancellationToken ct = default);
}

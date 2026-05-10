// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Reconciles the final summary against final verified finding outcomes.
/// </summary>
public interface ISummaryReconciliationService
{
    /// <summary>
    ///     Reconciles the original summary against final-gated finding outcomes.
    /// </summary>
    /// <param name="originalSummary">Summary produced before final gating.</param>
    /// <param name="findings">Candidate findings from the review run.</param>
    /// <param name="decisions">Final-gate decisions applied to those findings.</param>
    /// <returns>The reconciliation result.</returns>
    SummaryReconciliationResult Reconcile(
        string originalSummary,
        IReadOnlyList<CandidateReviewFinding> findings,
        IReadOnlyList<FinalGateDecision> decisions);
}

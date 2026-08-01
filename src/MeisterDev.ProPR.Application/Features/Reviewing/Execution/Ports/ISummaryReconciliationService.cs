// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

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
    /// <param name="summaryFindingIds">
    ///     The identifiers of the findings the summary describes, as declared by synthesis. When non-empty these
    ///     decide whether the summary describes a dropped finding, which holds however the summary is worded and
    ///     whatever language it is written in. Empty means synthesis declared nothing, and the implementation
    ///     falls back to comparing wording.
    /// </param>
    /// <returns>The reconciliation result.</returns>
    SummaryReconciliationResult Reconcile(
        string originalSummary,
        IReadOnlyList<CandidateReviewFinding> findings,
        IReadOnlyList<FinalGateDecision> decisions,
        IReadOnlyList<string>? summaryFindingIds = null);
}

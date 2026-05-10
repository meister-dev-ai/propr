// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.ObjectModel;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Aggregate final-gate summary payload for one review run.
/// </summary>
/// <param name="CandidateCount">Total number of candidate findings evaluated.</param>
/// <param name="PublishCount">Number of findings published normally.</param>
/// <param name="SummaryOnlyCount">Number of findings reduced to summary-only output.</param>
/// <param name="DropCount">Number of findings dropped entirely.</param>
/// <param name="CategoryCounts">Per-category counts for evaluated findings.</param>
/// <param name="InvariantBlockedCount">Number of decisions blocked by invariants.</param>
/// <param name="OriginalSummary">Original summary before reconciliation.</param>
/// <param name="FinalSummary">Final summary after reconciliation.</param>
/// <param name="SummaryRewritePerformed">Whether summary reconciliation rewrote the summary.</param>
/// <param name="DroppedFindingIds">Identifiers of findings dropped from the final output.</param>
/// <param name="SummaryOnlyFindingIds">Identifiers of findings kept only in the summary.</param>
/// <param name="SummaryRuleSource">Rule source used by summary reconciliation.</param>
public sealed record RecordedFinalGateSummary(
    int CandidateCount,
    int PublishCount,
    int SummaryOnlyCount,
    int DropCount,
    IReadOnlyDictionary<string, int> CategoryCounts,
    int InvariantBlockedCount,
    string? OriginalSummary,
    string? FinalSummary,
    bool SummaryRewritePerformed,
    IReadOnlyList<string>? DroppedFindingIds,
    IReadOnlyList<string>? SummaryOnlyFindingIds,
    string? SummaryRuleSource)
{
    /// <summary>
    ///     Builds an aggregate final-gate summary from findings, decisions, and optional summary reconciliation.
    /// </summary>
    /// <param name="findings">Candidate findings included in the review run.</param>
    /// <param name="decisions">Final-gate decisions produced for the findings.</param>
    /// <param name="reconciliation">Optional summary reconciliation result.</param>
    /// <returns>The aggregated final-gate summary payload.</returns>
    public static RecordedFinalGateSummary FromFindingsAndDecisions(
        IReadOnlyList<CandidateReviewFinding> findings,
        IReadOnlyList<FinalGateDecision> decisions,
        SummaryReconciliationResult? reconciliation = null)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(decisions);

        var publishCount = decisions.Count(decision => string.Equals(decision.Disposition, FinalGateDecision.PublishDisposition, StringComparison.Ordinal));
        var summaryOnlyCount = decisions.Count(decision => string.Equals(
            decision.Disposition,
            FinalGateDecision.SummaryOnlyDisposition,
            StringComparison.Ordinal));
        var dropCount = decisions.Count(decision => string.Equals(decision.Disposition, FinalGateDecision.DropDisposition, StringComparison.Ordinal));
        var categoryCounts = new ReadOnlyDictionary<string, int>(
            findings
                .GroupBy(finding => finding.Category, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal));
        var invariantBlockedCount = decisions.Count(decision => decision.BlockedInvariantIds.Count > 0);

        return new RecordedFinalGateSummary(
            findings.Count,
            publishCount,
            summaryOnlyCount,
            dropCount,
            categoryCounts,
            invariantBlockedCount,
            reconciliation?.OriginalSummary,
            reconciliation?.FinalSummary,
            reconciliation?.RewritePerformed ?? false,
            reconciliation?.DroppedFindingIds,
            reconciliation?.SummaryOnlyFindingIds,
            reconciliation?.RuleSource);
    }
}

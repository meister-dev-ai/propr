// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.ObjectModel;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Aggregate final-gate summary payload for one review run.
/// </summary>
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

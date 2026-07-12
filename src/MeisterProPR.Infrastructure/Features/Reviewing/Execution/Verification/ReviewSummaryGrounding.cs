// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;

/// <summary>
///     Assembles the deterministic footer appended to a review summary once verification has run:
///     the publishable-finding count, the "in pre-existing code" note, the summary-only findings list,
///     and the "no findings remained" line. This is the single owner of that footer so it is never
///     rendered more than once. <see cref="SummaryReconciliationService" /> is responsible only for the
///     narrative it grounds; it deliberately does not emit any of this footer.
/// </summary>
internal static class ReviewSummaryGrounding
{
    /// <summary>
    ///     Grounds a reconciled summary against the final deterministic dispositions by appending the
    ///     footer derived from <paramref name="gateDecisions" />. Returns the reconciliation unchanged when
    ///     there is nothing to append.
    /// </summary>
    public static SummaryReconciliationResult Ground(
        SummaryReconciliationResult reconciliation,
        IReadOnlyList<CandidateReviewFinding> candidateFindings,
        IReadOnlyList<FinalGateDecision> gateDecisions)
    {
        ArgumentNullException.ThrowIfNull(reconciliation);
        ArgumentNullException.ThrowIfNull(candidateFindings);
        ArgumentNullException.ThrowIfNull(gateDecisions);

        var publishCount = gateDecisions.Count(decision =>
            string.Equals(decision.Disposition, FinalGateDecision.PublishDisposition, StringComparison.Ordinal));

        var summaryOnlyItems = gateDecisions
            .Where(decision => string.Equals(decision.Disposition, FinalGateDecision.SummaryOnlyDisposition, StringComparison.Ordinal))
            .Select(decision => decision.SummaryText)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var outsideChangeCount = CountRetainedOutsideChangeFindings(candidateFindings, gateDecisions);

        var groundedSummary = BuildGroundedSummary(reconciliation.FinalSummary, publishCount, summaryOnlyItems, outsideChangeCount);
        if (string.Equals(reconciliation.FinalSummary, groundedSummary, StringComparison.Ordinal))
        {
            return reconciliation;
        }

        return new SummaryReconciliationResult(
            reconciliation.OriginalSummary,
            groundedSummary,
            reconciliation.DroppedFindingIds,
            reconciliation.SummaryOnlyFindingIds,
            true,
            "deterministic_summary_grounding");
    }

    /// <summary>
    ///     Counts retained (published) findings whose anchor line is classified as outside the pull request's
    ///     changed-line ranges, so the grounded summary can note that they live in pre-existing code.
    /// </summary>
    private static int CountRetainedOutsideChangeFindings(
        IReadOnlyList<CandidateReviewFinding> candidateFindings,
        IReadOnlyList<FinalGateDecision> gateDecisions)
    {
        var publishedFindingIds = gateDecisions
            .Where(decision => string.Equals(decision.Disposition, FinalGateDecision.PublishDisposition, StringComparison.Ordinal))
            .Select(decision => decision.FindingId)
            .ToHashSet(StringComparer.Ordinal);

        return candidateFindings.Count(finding =>
            finding.ScopeRelation == ChangedLineRelation.OutsideChange &&
            publishedFindingIds.Contains(finding.FindingId));
    }

    private static string BuildGroundedSummary(
        string reconciledSummary,
        int publishCount,
        IReadOnlyList<string> summaryOnlyItems,
        int outsideChangeCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reconciledSummary);

        var sb = new StringBuilder(reconciledSummary.Trim());

        if (publishCount == 0 && summaryOnlyItems.Count == 0)
        {
            sb.AppendLine()
                .AppendLine()
                .AppendLine("**No publishable or summary-only findings remained after verification.**");

            return sb.ToString();
        }

        if (publishCount > 0)
        {
            sb.AppendLine()
                .AppendLine()
                .AppendLine($"**Verification retained {publishCount} publishable finding{(publishCount == 1 ? string.Empty : "s")}.**");

            if (outsideChangeCount > 0)
            {
                sb.AppendLine()
                    .AppendLine($"_{outsideChangeCount} of these {(outsideChangeCount == 1 ? "is" : "are")} in pre-existing code outside this change._");
            }
        }

        if (summaryOnlyItems.Count > 0)
        {
            if (publishCount == 0)
            {
                sb.AppendLine()
                    .AppendLine()
                    .AppendLine("**No publishable findings remained after verification.**");
            }

            sb.AppendLine()
                .AppendLine()
                .AppendLine("**Summary-only findings:**");

            foreach (var item in summaryOnlyItems)
            {
                sb.AppendLine($"- {item}");
            }
        }

        return sb.ToString();
    }
}

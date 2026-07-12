// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;

/// <summary>
///     Reconciles the final review summary narrative against final deterministic dispositions.
///     This service owns only the narrative: it keeps the model's prose unless that prose references
///     a finding that verification dropped, in which case it replaces the prose with a neutral notice
///     so the published summary never claims an issue that was ruled out. The deterministic footer
///     (publishable counts, summary-only findings, the "no findings remained" line, the outside-change
///     note) is appended once, downstream, by the synthesis grounding step — it is intentionally not
///     produced here, to avoid emitting the same footer twice.
/// </summary>
public sealed class SummaryReconciliationService : ISummaryReconciliationService
{
    public SummaryReconciliationResult Reconcile(
        string originalSummary,
        IReadOnlyList<CandidateReviewFinding> findings,
        IReadOnlyList<FinalGateDecision> decisions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalSummary);
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(decisions);

        var findingsById = findings.ToDictionary(finding => finding.FindingId, StringComparer.Ordinal);

        var droppedFindingIds = decisions
            .Where(decision => string.Equals(decision.Disposition, FinalGateDecision.DropDisposition, StringComparison.Ordinal))
            .Select(decision => decision.FindingId)
            .ToArray();
        var summaryOnlyFindingIds = decisions
            .Where(decision => string.Equals(decision.Disposition, FinalGateDecision.SummaryOnlyDisposition, StringComparison.Ordinal))
            .Select(decision => decision.FindingId)
            .ToArray();

        var rewriteNeeded = droppedFindingIds
            .Select(id => findingsById.GetValueOrDefault(id))
            .Where(finding => finding is not null)
            .Any(finding => SummaryLikelyMentionsFinding(originalSummary, finding!));

        if (!rewriteNeeded)
        {
            return new SummaryReconciliationResult(
                originalSummary,
                originalSummary,
                droppedFindingIds,
                summaryOnlyFindingIds,
                false,
                "deterministic_summary_passthrough");
        }

        // The model prose references a now-dropped finding; replace it with a neutral notice.
        // rewriteNeeded is only true when at least one dropped finding is mentioned, so dropCount >= 1.
        return new SummaryReconciliationResult(
            originalSummary,
            BuildDroppedFindingsNotice(droppedFindingIds.Length),
            droppedFindingIds,
            summaryOnlyFindingIds,
            true,
            "deterministic_summary_rewrite");
    }

    private static string BuildDroppedFindingsNotice(int dropCount)
    {
        return dropCount == 1
            ? "1 candidate finding was dropped during verification."
            : $"{dropCount} candidate findings were dropped during verification.";
    }

    private static bool SummaryLikelyMentionsFinding(string summary, CandidateReviewFinding finding)
    {
        var needles = new List<string>();
        if (!string.IsNullOrWhiteSpace(finding.Message))
        {
            needles.Add(finding.Message);
            needles.AddRange(
                finding.Message
                    .Split([' ', ',', '.', ':', ';', '-', '(', ')'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Where(token => token.Length >= 4));
        }

        return needles
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Any(needle => summary.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;

/// <summary>
///     Reconciles the final review summary against final deterministic dispositions.
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
        var summaryOnlyDecisions = decisions
            .Where(decision => string.Equals(decision.Disposition, FinalGateDecision.SummaryOnlyDisposition, StringComparison.Ordinal))
            .ToArray();
        var summaryOnlyFindingIds = summaryOnlyDecisions
            .Select(decision => decision.FindingId)
            .ToArray();

        var summaryOnlyItems = summaryOnlyDecisions
            .Select(decision => decision.SummaryText)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var rewriteNeeded = droppedFindingIds
            .Select(id => findingsById.GetValueOrDefault(id))
            .Where(finding => finding is not null)
            .Any(finding => SummaryLikelyMentionsFinding(originalSummary, finding!));

        if (!rewriteNeeded)
        {
            var appendedSummary = AppendSummaryOnlyFindings(originalSummary, summaryOnlyItems);
            return new SummaryReconciliationResult(
                originalSummary,
                appendedSummary,
                droppedFindingIds,
                summaryOnlyFindingIds,
                false,
                "deterministic_summary_append");
        }

        var publishCount = decisions.Count(decision => string.Equals(decision.Disposition, FinalGateDecision.PublishDisposition, StringComparison.Ordinal));
        if (publishCount == 0 && summaryOnlyItems.Count == 0)
        {
            return new SummaryReconciliationResult(
                originalSummary,
                "No publishable or summary-only findings remained after verification.",
                droppedFindingIds,
                summaryOnlyFindingIds,
                true,
                "deterministic_summary_rewrite");
        }

        var rewrittenSummary = BuildRewriteSummary(publishCount, droppedFindingIds.Length, summaryOnlyItems);
        return new SummaryReconciliationResult(
            originalSummary,
            rewrittenSummary,
            droppedFindingIds,
            summaryOnlyFindingIds,
            true,
            "deterministic_summary_rewrite");
    }

    private static string BuildRewriteSummary(int publishCount, int dropCount, IReadOnlyList<string> summaryOnlyItems)
    {
        var builder = new StringBuilder();

        if (publishCount > 0)
        {
            builder.Append($"Verification retained {publishCount} publishable finding");
            builder.Append(publishCount == 1 ? "." : "s.");
        }

        if (dropCount > 0)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append($"{dropCount} candidate finding");
            builder.Append(dropCount == 1 ? " was dropped during verification." : "s were dropped during verification.");
        }

        if (summaryOnlyItems.Count > 0)
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine();
            }

            builder.Append("Summary-only findings:");
            foreach (var item in summaryOnlyItems)
            {
                builder.AppendLine();
                builder.Append("- ");
                builder.Append(item);
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string AppendSummaryOnlyFindings(string baseSummary, IReadOnlyList<string> summaryOnlyItems)
    {
        if (summaryOnlyItems.Count == 0)
        {
            return baseSummary;
        }

        var builder = new StringBuilder();
        builder.Append(baseSummary.TrimEnd());
        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine("Summary-only findings:");
        foreach (var item in summaryOnlyItems)
        {
            builder.Append("- ");
            builder.AppendLine(item);
        }

        return builder.ToString().TrimEnd();
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

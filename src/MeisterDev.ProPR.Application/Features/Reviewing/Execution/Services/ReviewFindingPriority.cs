// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>Orders presentation by assessed risk while preserving stored finding identity and order.</summary>
public static class ReviewFindingPriority
{
    /// <summary>Returns stable descending risk order. Unassessed findings remain explicitly unassessed.</summary>
    public static IEnumerable<ReviewComment> Ordered(ReviewResult result) =>
        result.Comments.OrderByDescending(comment => comment.RiskAssessment?.Severity ?? FindingRiskSeverity.Unknown);

    /// <summary>Identifies every confirmed defect tied at the highest assessed defect risk.</summary>
    public static bool IsHighest(ReviewResult result, ReviewComment comment) =>
        comment.Severity == CommentSeverity.Error && comment.RiskAssessment?.Severity is > FindingRiskSeverity.Unknown
                                                  && comment.RiskAssessment.Severity == HighestDefectRisk(result);

    /// <summary>Names the most urgent assessed confirmed defect before the model's narrative.</summary>
    public static string FormatLead(ReviewResult result, Func<string, string>? renderText = null)
    {
        renderText ??= ReviewFindingTextFormatter.RenderPlainText;
        var highest = Ordered(result).FirstOrDefault(comment => IsHighest(result, comment));
        if (highest is null)
        {
            return string.Empty;
        }

        var claim = highest.Message.Split(["\n\n", "\r\n\r\n"], StringSplitOptions.None)[0].ReplaceLineEndings(" ");
        if (claim.Length > 400)
        {
            claim = claim[..400] + "…";
        }

        return $"**Fix first · Risk: {highest.RiskAssessment!.Severity}** · "
               + ReviewSummaryTextFormatter.FormatLocation(highest, renderText, 160) + ": " + renderText(claim) + "\n\n";
    }

    private static FindingRiskSeverity HighestDefectRisk(ReviewResult result) =>
        result.Comments.Where(comment => comment.Severity == CommentSeverity.Error)
            .Select(comment => comment.RiskAssessment?.Severity ?? FindingRiskSeverity.Unknown).DefaultIfEmpty().Max();
}

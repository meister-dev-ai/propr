// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Result of reconciling the final summary with post-verification outcomes.
/// </summary>
public sealed record SummaryReconciliationResult
{
    public SummaryReconciliationResult(
        string originalSummary,
        string finalSummary,
        IReadOnlyList<string>? droppedFindingIds,
        IReadOnlyList<string>? summaryOnlyFindingIds,
        bool rewritePerformed,
        string ruleSource)
    {
        if (string.IsNullOrWhiteSpace(originalSummary))
        {
            throw new ArgumentException("Original summary is required.", nameof(originalSummary));
        }

        if (string.IsNullOrWhiteSpace(finalSummary))
        {
            throw new ArgumentException("Final summary is required.", nameof(finalSummary));
        }

        if (string.IsNullOrWhiteSpace(ruleSource))
        {
            throw new ArgumentException("Rule source is required.", nameof(ruleSource));
        }

        this.OriginalSummary = originalSummary;
        this.FinalSummary = finalSummary;
        this.DroppedFindingIds = droppedFindingIds?.Where(id => !string.IsNullOrWhiteSpace(id)).ToArray() ?? [];
        this.SummaryOnlyFindingIds = summaryOnlyFindingIds?.Where(id => !string.IsNullOrWhiteSpace(id)).ToArray() ?? [];
        this.RewritePerformed = rewritePerformed;
        this.RuleSource = ruleSource;
    }

    public string OriginalSummary { get; }

    public string FinalSummary { get; }

    public IReadOnlyList<string> DroppedFindingIds { get; }

    public IReadOnlyList<string> SummaryOnlyFindingIds { get; }

    public bool RewritePerformed { get; }

    public string RuleSource { get; }
}

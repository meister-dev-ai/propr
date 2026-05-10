// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Result of reconciling the final summary with post-verification outcomes.
/// </summary>
public sealed record SummaryReconciliationResult
{
    /// <summary>
    ///     Initializes the result of reconciling the summary after verification.
    /// </summary>
    /// <param name="originalSummary">Summary before reconciliation.</param>
    /// <param name="finalSummary">Summary after reconciliation.</param>
    /// <param name="droppedFindingIds">Identifiers of findings dropped from the final output.</param>
    /// <param name="summaryOnlyFindingIds">Identifiers of findings kept only in the summary.</param>
    /// <param name="rewritePerformed">Whether reconciliation rewrote the summary text.</param>
    /// <param name="ruleSource">Rule source that produced the reconciliation.</param>
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

    /// <summary>
    ///     Gets the summary before reconciliation.
    /// </summary>
    public string OriginalSummary { get; }

    /// <summary>
    ///     Gets the summary after reconciliation.
    /// </summary>
    public string FinalSummary { get; }

    /// <summary>
    ///     Gets identifiers of findings dropped from the final output.
    /// </summary>
    public IReadOnlyList<string> DroppedFindingIds { get; }

    /// <summary>
    ///     Gets identifiers of findings kept only in the summary.
    /// </summary>
    public IReadOnlyList<string> SummaryOnlyFindingIds { get; }

    /// <summary>
    ///     Gets a value indicating whether reconciliation rewrote the summary text.
    /// </summary>
    public bool RewritePerformed { get; }

    /// <summary>
    ///     Gets the rule source that produced the reconciliation.
    /// </summary>
    public string RuleSource { get; }
}

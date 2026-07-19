// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Deterministic final disposition for a candidate finding.
/// </summary>
public sealed record FinalGateDecision
{
    /// <summary>
    ///     Disposition used when a finding should be published normally.
    /// </summary>
    public const string PublishDisposition = "Publish";

    /// <summary>
    ///     Disposition used when a finding should only affect the summary.
    /// </summary>
    public const string SummaryOnlyDisposition = "SummaryOnly";

    /// <summary>
    ///     Disposition used when a finding should be dropped.
    /// </summary>
    public const string DropDisposition = "Drop";

    /// <summary>
    ///     Initializes a deterministic final-gate decision for a finding.
    /// </summary>
    /// <param name="findingId">Identifier of the finding being decided.</param>
    /// <param name="disposition">Final disposition selected for the finding.</param>
    /// <param name="reasonCodes">Reason codes that justify the decision.</param>
    /// <param name="ruleSource">Rule set or evaluator that produced the decision.</param>
    /// <param name="blockedInvariantIds">Blocking invariants associated with the decision.</param>
    /// <param name="evidenceSnapshot">Evidence snapshot used while making the decision.</param>
    /// <param name="summaryText">Optional summary text emitted for summary-only decisions.</param>
    /// <param name="publicationNote">
    ///     Optional note appended to the published comment body when the decision publishes. Used by
    ///     finalization checks to annotate an otherwise-unchanged finding (e.g. an unverified ERROR).
    /// </param>
    public FinalGateDecision(
        string findingId,
        string disposition,
        IReadOnlyList<string>? reasonCodes,
        string ruleSource,
        IReadOnlyList<string>? blockedInvariantIds,
        EvidenceReference? evidenceSnapshot,
        string? summaryText,
        string? publicationNote = null)
    {
        if (string.IsNullOrWhiteSpace(findingId))
        {
            throw new ArgumentException("Finding ID is required.", nameof(findingId));
        }

        if (string.IsNullOrWhiteSpace(disposition))
        {
            throw new ArgumentException("Disposition is required.", nameof(disposition));
        }

        if (string.IsNullOrWhiteSpace(ruleSource))
        {
            throw new ArgumentException("Rule source is required.", nameof(ruleSource));
        }

        var normalizedReasonCodes = reasonCodes?.Where(code => !string.IsNullOrWhiteSpace(code)).ToArray() ?? [];
        if (normalizedReasonCodes.Length == 0)
        {
            throw new ArgumentException("At least one reason code is required.", nameof(reasonCodes));
        }

        if (string.Equals(disposition, SummaryOnlyDisposition, StringComparison.Ordinal) && string.IsNullOrWhiteSpace(summaryText))
        {
            throw new ArgumentException("SummaryOnly decisions require summary text.", nameof(summaryText));
        }

        this.FindingId = findingId;
        this.Disposition = disposition;
        this.ReasonCodes = normalizedReasonCodes;
        this.RuleSource = ruleSource;
        this.BlockedInvariantIds = blockedInvariantIds?.Where(id => !string.IsNullOrWhiteSpace(id)).ToArray() ?? [];
        this.EvidenceSnapshot = evidenceSnapshot;
        this.SummaryText = string.Equals(disposition, SummaryOnlyDisposition, StringComparison.Ordinal)
            ? summaryText
            : null;
        this.PublicationNote = string.IsNullOrWhiteSpace(publicationNote) ? null : publicationNote;
    }

    /// <summary>
    ///     Gets the identifier of the finding being decided.
    /// </summary>
    public string FindingId { get; }

    /// <summary>
    ///     Gets the final disposition selected for the finding.
    /// </summary>
    public string Disposition { get; }

    /// <summary>
    ///     Gets the reason codes that justify the decision.
    /// </summary>
    public IReadOnlyList<string> ReasonCodes { get; }

    /// <summary>
    ///     Gets the rule set or evaluator that produced the decision.
    /// </summary>
    public string RuleSource { get; }

    /// <summary>
    ///     Gets the blocking invariants associated with the decision.
    /// </summary>
    public IReadOnlyList<string> BlockedInvariantIds { get; }

    /// <summary>
    ///     Gets the evidence snapshot used while making the decision.
    /// </summary>
    public EvidenceReference? EvidenceSnapshot { get; }

    /// <summary>
    ///     Gets the optional summary text emitted for summary-only decisions.
    /// </summary>
    public string? SummaryText { get; }

    /// <summary>
    ///     Gets the optional note appended to the published comment body when this decision publishes.
    ///     <see langword="null" /> when no note applies.
    /// </summary>
    public string? PublicationNote { get; }

    /// <summary>
    ///     Converts the decision into a persisted final-gate decision record.
    /// </summary>
    /// <param name="finding">Finding metadata paired with the decision.</param>
    /// <param name="includedInFinalSummary">Whether the finding was included in the final summary.</param>
    /// <returns>The persisted representation of the decision.</returns>
    public RecordedFinalGateDecision ToRecordedDecision(CandidateReviewFinding finding, bool includedInFinalSummary = false)
    {
        ArgumentNullException.ThrowIfNull(finding);

        return new RecordedFinalGateDecision(
            this.FindingId,
            this.Disposition,
            finding.Category,
            finding.Provenance,
            this.EvidenceSnapshot,
            this.ReasonCodes,
            this.BlockedInvariantIds,
            this.RuleSource,
            this.SummaryText,
            includedInFinalSummary);
    }
}

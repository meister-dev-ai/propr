// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Deterministic final disposition for a candidate finding.
/// </summary>
public sealed record FinalGateDecision
{
    public const string PublishDisposition = "Publish";
    public const string SummaryOnlyDisposition = "SummaryOnly";
    public const string DropDisposition = "Drop";

    public FinalGateDecision(
        string findingId,
        string disposition,
        IReadOnlyList<string>? reasonCodes,
        string ruleSource,
        IReadOnlyList<string>? blockedInvariantIds,
        EvidenceReference? evidenceSnapshot,
        string? summaryText)
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
    }

    public string FindingId { get; }

    public string Disposition { get; }

    public IReadOnlyList<string> ReasonCodes { get; }

    public string RuleSource { get; }

    public IReadOnlyList<string> BlockedInvariantIds { get; }

    public EvidenceReference? EvidenceSnapshot { get; }

    public string? SummaryText { get; }

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

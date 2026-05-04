// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Bounded evidence collected for one verification claim.
/// </summary>
public sealed record EvidenceBundle
{
    public const string CompleteCoverage = "Complete";
    public const string PartialCoverage = "Partial";
    public const string MissingCoverage = "Missing";

    public EvidenceBundle(
        string claimId,
        IReadOnlyList<EvidenceItem>? evidenceItems,
        string coverageState,
        string? retrievalNotes = null,
        IReadOnlyList<EvidenceAttemptRecord>? evidenceAttempts = null,
        bool hasProCursorAttempt = false,
        string? proCursorResultStatus = null)
    {
        if (string.IsNullOrWhiteSpace(claimId))
        {
            throw new ArgumentException("Claim ID is required.", nameof(claimId));
        }

        if (string.IsNullOrWhiteSpace(coverageState))
        {
            throw new ArgumentException("Coverage state is required.", nameof(coverageState));
        }

        if (hasProCursorAttempt && string.IsNullOrWhiteSpace(proCursorResultStatus))
        {
            throw new ArgumentException("ProCursor result status is required when ProCursor was attempted.", nameof(proCursorResultStatus));
        }

        this.ClaimId = claimId;
        this.EvidenceItems = evidenceItems?.ToArray() ?? [];
        this.CoverageState = coverageState;
        this.RetrievalNotes = retrievalNotes;
        this.EvidenceAttempts = evidenceAttempts?.ToArray() ?? [];
        this.HasProCursorAttempt = hasProCursorAttempt;
        this.ProCursorResultStatus = proCursorResultStatus;
    }

    public string ClaimId { get; }

    public IReadOnlyList<EvidenceItem> EvidenceItems { get; }

    public string CoverageState { get; }

    public string? RetrievalNotes { get; }

    public IReadOnlyList<EvidenceAttemptRecord> EvidenceAttempts { get; }

    public bool HasProCursorAttempt { get; }

    public string? ProCursorResultStatus { get; }

    public bool HasCompleteCoverage => string.Equals(this.CoverageState, CompleteCoverage, StringComparison.Ordinal);
}

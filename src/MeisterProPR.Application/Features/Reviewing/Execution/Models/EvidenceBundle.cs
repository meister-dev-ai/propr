// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Bounded evidence collected for one verification claim.
/// </summary>
public sealed record EvidenceBundle
{
    /// <summary>
    ///     Coverage state indicating complete evidence coverage.
    /// </summary>
    public const string CompleteCoverage = "Complete";

    /// <summary>
    ///     Coverage state indicating partial evidence coverage.
    /// </summary>
    public const string PartialCoverage = "Partial";

    /// <summary>
    ///     Coverage state indicating missing evidence coverage.
    /// </summary>
    public const string MissingCoverage = "Missing";

    /// <summary>
    ///     Initializes bounded evidence collected for a verification claim.
    /// </summary>
    /// <param name="claimId">Identifier of the claim this evidence supports.</param>
    /// <param name="evidenceItems">Collected evidence items.</param>
    /// <param name="coverageState">Coverage state derived from the collected evidence.</param>
    /// <param name="retrievalNotes">Optional retrieval notes captured during evidence collection.</param>
    /// <param name="evidenceAttempts">Execution attempts made while collecting evidence.</param>
    /// <param name="hasProCursorAttempt">Whether ProCursor was used during evidence collection.</param>
    /// <param name="proCursorResultStatus">Optional ProCursor result status when ProCursor was attempted.</param>
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

    /// <summary>
    ///     Gets the identifier of the claim this bundle supports.
    /// </summary>
    public string ClaimId { get; }

    /// <summary>
    ///     Gets the collected evidence items.
    /// </summary>
    public IReadOnlyList<EvidenceItem> EvidenceItems { get; }

    /// <summary>
    ///     Gets the coverage state derived from the collected evidence.
    /// </summary>
    public string CoverageState { get; }

    /// <summary>
    ///     Gets retrieval notes captured during evidence collection.
    /// </summary>
    public string? RetrievalNotes { get; }

    /// <summary>
    ///     Gets the execution attempts made while collecting evidence.
    /// </summary>
    public IReadOnlyList<EvidenceAttemptRecord> EvidenceAttempts { get; }

    /// <summary>
    ///     Gets a value indicating whether ProCursor was used during evidence collection.
    /// </summary>
    public bool HasProCursorAttempt { get; }

    /// <summary>
    ///     Gets the ProCursor result status when ProCursor was attempted.
    /// </summary>
    public string? ProCursorResultStatus { get; }

    /// <summary>
    ///     Gets a value indicating whether the bundle has complete coverage.
    /// </summary>
    public bool HasCompleteCoverage => string.Equals(this.CoverageState, CompleteCoverage, StringComparison.Ordinal);
}

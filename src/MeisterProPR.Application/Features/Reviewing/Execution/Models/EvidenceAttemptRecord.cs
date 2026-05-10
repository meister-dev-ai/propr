// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     One evidence-source attempt made while collecting bounded claim evidence.
/// </summary>
public sealed record EvidenceAttemptRecord
{
    /// <summary>
    ///     Source family used for direct file-content inspection.
    /// </summary>
    public const string FileContentSource = "FileContent";

    /// <summary>
    ///     Source family used for repository-structure inspection.
    /// </summary>
    public const string RepositoryStructureSource = "RepositoryStructure";

    /// <summary>
    ///     Source family used for ProCursor knowledge retrieval.
    /// </summary>
    public const string ProCursorKnowledgeSource = "ProCursorKnowledge";

    /// <summary>
    ///     Source family used for ProCursor symbol retrieval.
    /// </summary>
    public const string ProCursorSymbolSource = "ProCursorSymbol";

    /// <summary>
    ///     Status used when an evidence attempt succeeded.
    /// </summary>
    public const string SucceededStatus = "Succeeded";

    /// <summary>
    ///     Status used when an evidence attempt returned no results.
    /// </summary>
    public const string EmptyStatus = "Empty";

    /// <summary>
    ///     Status used when an evidence attempt could not reach its dependency.
    /// </summary>
    public const string UnavailableStatus = "Unavailable";

    /// <summary>
    ///     Status used when an evidence attempt failed.
    /// </summary>
    public const string FailedStatus = "Failed";

    /// <summary>
    ///     Status used when an evidence attempt was skipped.
    /// </summary>
    public const string SkippedStatus = "Skipped";

    /// <summary>
    ///     Coverage impact used when the attempt expanded available evidence.
    /// </summary>
    public const string ExpandedCoverageImpact = "Expanded";

    /// <summary>
    ///     Coverage impact used when the attempt left coverage unchanged.
    /// </summary>
    public const string NoChangeCoverageImpact = "NoChange";

    /// <summary>
    ///     Coverage impact used when the attempt reduced confidence in coverage.
    /// </summary>
    public const string ReducedConfidenceCoverageImpact = "ReducedConfidence";

    /// <summary>
    ///     Initializes an evidence-collection attempt record.
    /// </summary>
    /// <param name="attemptId">Stable identifier for the attempt.</param>
    /// <param name="claimId">Identifier of the claim being serviced.</param>
    /// <param name="sourceFamily">Evidence source family used by the attempt.</param>
    /// <param name="attemptOrder">Execution order of the attempt.</param>
    /// <param name="status">Outcome status of the attempt.</param>
    /// <param name="scopeSummary">Human-readable summary of the evidence scope.</param>
    /// <param name="coverageImpact">Coverage impact reported by the attempt.</param>
    /// <param name="payloadReference">Optional payload reference for the retrieved evidence.</param>
    /// <param name="failureReason">Optional failure reason when the attempt did not succeed.</param>
    public EvidenceAttemptRecord(
        string attemptId,
        string claimId,
        string sourceFamily,
        int attemptOrder,
        string status,
        string scopeSummary,
        string coverageImpact,
        string? payloadReference = null,
        string? failureReason = null)
    {
        if (string.IsNullOrWhiteSpace(attemptId))
        {
            throw new ArgumentException("Attempt ID is required.", nameof(attemptId));
        }

        if (string.IsNullOrWhiteSpace(claimId))
        {
            throw new ArgumentException("Claim ID is required.", nameof(claimId));
        }

        if (string.IsNullOrWhiteSpace(sourceFamily))
        {
            throw new ArgumentException("Source family is required.", nameof(sourceFamily));
        }

        if (attemptOrder <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptOrder), "Attempt order must be positive.");
        }

        if (string.IsNullOrWhiteSpace(status))
        {
            throw new ArgumentException("Status is required.", nameof(status));
        }

        if (string.IsNullOrWhiteSpace(scopeSummary))
        {
            throw new ArgumentException("Scope summary is required.", nameof(scopeSummary));
        }

        if (string.IsNullOrWhiteSpace(coverageImpact))
        {
            throw new ArgumentException("Coverage impact is required.", nameof(coverageImpact));
        }

        this.AttemptId = attemptId;
        this.ClaimId = claimId;
        this.SourceFamily = sourceFamily;
        this.AttemptOrder = attemptOrder;
        this.Status = status;
        this.ScopeSummary = scopeSummary;
        this.CoverageImpact = coverageImpact;
        this.PayloadReference = payloadReference;
        this.FailureReason = failureReason;
    }

    /// <summary>
    ///     Gets the stable identifier for the attempt.
    /// </summary>
    public string AttemptId { get; }

    /// <summary>
    ///     Gets the identifier of the claim being serviced.
    /// </summary>
    public string ClaimId { get; }

    /// <summary>
    ///     Gets the evidence source family used by the attempt.
    /// </summary>
    public string SourceFamily { get; }

    /// <summary>
    ///     Gets the execution order of the attempt.
    /// </summary>
    public int AttemptOrder { get; }

    /// <summary>
    ///     Gets the outcome status of the attempt.
    /// </summary>
    public string Status { get; }

    /// <summary>
    ///     Gets the human-readable summary of the evidence scope.
    /// </summary>
    public string ScopeSummary { get; }

    /// <summary>
    ///     Gets the coverage impact reported by the attempt.
    /// </summary>
    public string CoverageImpact { get; }

    /// <summary>
    ///     Gets the payload reference for retrieved evidence when available.
    /// </summary>
    public string? PayloadReference { get; }

    /// <summary>
    ///     Gets the failure reason when the attempt did not succeed.
    /// </summary>
    public string? FailureReason { get; }
}

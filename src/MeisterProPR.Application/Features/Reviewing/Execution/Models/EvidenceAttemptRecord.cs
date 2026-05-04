// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     One evidence-source attempt made while collecting bounded claim evidence.
/// </summary>
public sealed record EvidenceAttemptRecord
{
    public const string FileContentSource = "FileContent";
    public const string RepositoryStructureSource = "RepositoryStructure";
    public const string ProCursorKnowledgeSource = "ProCursorKnowledge";
    public const string ProCursorSymbolSource = "ProCursorSymbol";
    public const string SucceededStatus = "Succeeded";
    public const string EmptyStatus = "Empty";
    public const string UnavailableStatus = "Unavailable";
    public const string FailedStatus = "Failed";
    public const string SkippedStatus = "Skipped";
    public const string ExpandedCoverageImpact = "Expanded";
    public const string NoChangeCoverageImpact = "NoChange";
    public const string ReducedConfidenceCoverageImpact = "ReducedConfidence";

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

    public string AttemptId { get; }

    public string ClaimId { get; }

    public string SourceFamily { get; }

    public int AttemptOrder { get; }

    public string Status { get; }

    public string ScopeSummary { get; }

    public string CoverageImpact { get; }

    public string? PayloadReference { get; }

    public string? FailureReason { get; }
}

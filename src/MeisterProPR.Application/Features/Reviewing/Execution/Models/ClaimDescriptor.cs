// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Structured claim extracted from a review finding for deterministic or evidence-backed verification.
/// </summary>
public sealed record ClaimDescriptor
{
    public const string LocalStage = "Local";
    public const string PrLevelStage = "PrLevel";
    public const string DeterministicOnlyMode = "DeterministicOnly";
    public const string NeedsEvidenceMode = "NeedsEvidence";
    public const string SkipHeavyVerificationMode = "SkipHeavyVerification";
    public const string CodeContractFamily = "CodeContract";
    public const string DataFlowFamily = "DataFlow";
    public const string ApiOrSymbolUsageFamily = "ApiOrSymbolUsage";
    public const string ConfigurationOrWiringFamily = "ConfigurationOrWiring";
    public const string CrossFileConsistencyFamily = "CrossFileConsistency";
    public const string TestAdequacyFamily = "TestAdequacy";
    public const string DocumentationAccuracyFamily = "DocumentationAccuracy";
    public const string OperationalRiskFamily = "OperationalRisk";

    public ClaimDescriptor(
        string claimId,
        string findingId,
        string stage,
        string claimKind,
        string assertionText,
        CommentSeverity severity,
        string verificationMode,
        string claimFamily,
        string? subjectKind = null,
        string? subjectIdentifier = null,
        string? anchorFilePath = null,
        int? anchorLineNumber = null,
        bool requiresCrossFileEvidence = false,
        bool requiresSymbolEvidence = false)
    {
        if (string.IsNullOrWhiteSpace(claimId))
        {
            throw new ArgumentException("Claim ID is required.", nameof(claimId));
        }

        if (string.IsNullOrWhiteSpace(findingId))
        {
            throw new ArgumentException("Finding ID is required.", nameof(findingId));
        }

        if (string.IsNullOrWhiteSpace(stage))
        {
            throw new ArgumentException("Stage is required.", nameof(stage));
        }

        if (string.IsNullOrWhiteSpace(claimKind))
        {
            throw new ArgumentException("Claim kind is required.", nameof(claimKind));
        }

        if (string.IsNullOrWhiteSpace(assertionText))
        {
            throw new ArgumentException("Assertion text is required.", nameof(assertionText));
        }

        if (string.IsNullOrWhiteSpace(verificationMode))
        {
            throw new ArgumentException("Verification mode is required.", nameof(verificationMode));
        }

        if (string.IsNullOrWhiteSpace(claimFamily))
        {
            throw new ArgumentException("Claim family is required.", nameof(claimFamily));
        }

        if (anchorLineNumber is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(anchorLineNumber), "Anchor line number must be positive when provided.");
        }

        this.ClaimId = claimId;
        this.FindingId = findingId;
        this.Stage = stage;
        this.ClaimKind = claimKind;
        this.ClaimFamily = claimFamily;
        this.SubjectKind = subjectKind;
        this.SubjectIdentifier = subjectIdentifier;
        this.AnchorFilePath = anchorFilePath;
        this.AnchorLineNumber = anchorLineNumber;
        this.AssertionText = assertionText;
        this.Severity = severity;
        this.VerificationMode = verificationMode;
        this.RequiresCrossFileEvidence = requiresCrossFileEvidence;
        this.RequiresSymbolEvidence = requiresSymbolEvidence;
    }

    public string ClaimId { get; }

    public string FindingId { get; }

    public string Stage { get; }

    public string ClaimKind { get; }

    public string ClaimFamily { get; }

    public string? SubjectKind { get; }

    public string? SubjectIdentifier { get; }

    public string? AnchorFilePath { get; }

    public int? AnchorLineNumber { get; }

    public string AssertionText { get; }

    public CommentSeverity Severity { get; }

    public string VerificationMode { get; }

    public bool RequiresCrossFileEvidence { get; }

    public bool RequiresSymbolEvidence { get; }
}

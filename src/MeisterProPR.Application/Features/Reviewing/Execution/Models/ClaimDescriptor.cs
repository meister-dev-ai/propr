// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Structured claim extracted from a review finding for deterministic or evidence-backed verification.
/// </summary>
public sealed record ClaimDescriptor
{
    /// <summary>
    ///     Review stage representing local, file-scoped verification.
    /// </summary>
    public const string LocalStage = "Local";

    /// <summary>
    ///     Review stage representing pull-request-level verification.
    /// </summary>
    public const string PrLevelStage = "PrLevel";

    /// <summary>
    ///     Verification mode that relies only on deterministic checks.
    /// </summary>
    public const string DeterministicOnlyMode = "DeterministicOnly";

    /// <summary>
    ///     Verification mode that requires collected evidence.
    /// </summary>
    public const string NeedsEvidenceMode = "NeedsEvidence";

    /// <summary>
    ///     Verification mode that skips heavy verification steps.
    /// </summary>
    public const string SkipHeavyVerificationMode = "SkipHeavyVerification";

    /// <summary>
    ///     Claim family for code-contract assertions.
    /// </summary>
    public const string CodeContractFamily = "CodeContract";

    /// <summary>
    ///     Claim family for data-flow assertions.
    /// </summary>
    public const string DataFlowFamily = "DataFlow";

    /// <summary>
    ///     Claim family for API or symbol-usage assertions.
    /// </summary>
    public const string ApiOrSymbolUsageFamily = "ApiOrSymbolUsage";

    /// <summary>
    ///     Claim family for configuration or wiring assertions.
    /// </summary>
    public const string ConfigurationOrWiringFamily = "ConfigurationOrWiring";

    /// <summary>
    ///     Claim family for cross-file consistency assertions.
    /// </summary>
    public const string CrossFileConsistencyFamily = "CrossFileConsistency";

    /// <summary>
    ///     Claim family for test-adequacy assertions.
    /// </summary>
    public const string TestAdequacyFamily = "TestAdequacy";

    /// <summary>
    ///     Claim family for documentation-accuracy assertions.
    /// </summary>
    public const string DocumentationAccuracyFamily = "DocumentationAccuracy";

    /// <summary>
    ///     Claim family for operational-risk assertions.
    /// </summary>
    public const string OperationalRiskFamily = "OperationalRisk";

    /// <summary>
    ///     Support source used when repeated judgment agrees on the same evidence set.
    /// </summary>
    public const string JudgmentAgreementSupportSource = "JudgmentAgreement";

    /// <summary>
    ///     Initializes a structured claim extracted from a review finding.
    /// </summary>
    /// <param name="claimId">Stable identifier for the claim.</param>
    /// <param name="findingId">Identifier of the source finding.</param>
    /// <param name="stage">Review stage where the claim is verified.</param>
    /// <param name="claimKind">Machine-readable kind for the claim.</param>
    /// <param name="assertionText">Human-readable assertion captured from the finding.</param>
    /// <param name="severity">Severity associated with the claim.</param>
    /// <param name="verificationMode">Verification mode selected for the claim.</param>
    /// <param name="claimFamily">Family grouping for the claim.</param>
    /// <param name="subjectKind">Optional kind of subject being described.</param>
    /// <param name="subjectIdentifier">Optional identifier of the subject being described.</param>
    /// <param name="anchorFilePath">Optional repository-relative anchor file path.</param>
    /// <param name="anchorLineNumber">Optional anchor line number.</param>
    /// <param name="requiresCrossFileEvidence">Whether cross-file evidence is required.</param>
    /// <param name="requiresSymbolEvidence">Whether symbol-aware evidence is required.</param>
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

    /// <summary>
    ///     Gets the stable identifier for the claim.
    /// </summary>
    public string ClaimId { get; }

    /// <summary>
    ///     Gets the identifier of the source finding.
    /// </summary>
    public string FindingId { get; }

    /// <summary>
    ///     Gets the review stage where the claim is verified.
    /// </summary>
    public string Stage { get; }

    /// <summary>
    ///     Gets the machine-readable claim kind.
    /// </summary>
    public string ClaimKind { get; }

    /// <summary>
    ///     Gets the family grouping assigned to the claim.
    /// </summary>
    public string ClaimFamily { get; }

    /// <summary>
    ///     Gets the optional subject kind described by the claim.
    /// </summary>
    public string? SubjectKind { get; }

    /// <summary>
    ///     Gets the optional subject identifier described by the claim.
    /// </summary>
    public string? SubjectIdentifier { get; }

    /// <summary>
    ///     Gets the repository-relative anchor file path when available.
    /// </summary>
    public string? AnchorFilePath { get; }

    /// <summary>
    ///     Gets the anchor line number when available.
    /// </summary>
    public int? AnchorLineNumber { get; }

    /// <summary>
    ///     Gets the human-readable assertion text.
    /// </summary>
    public string AssertionText { get; }

    /// <summary>
    ///     Gets the severity associated with the claim.
    /// </summary>
    public CommentSeverity Severity { get; }

    /// <summary>
    ///     Gets the verification mode selected for the claim.
    /// </summary>
    public string VerificationMode { get; }

    /// <summary>
    ///     Gets a value indicating whether cross-file evidence is required.
    /// </summary>
    public bool RequiresCrossFileEvidence { get; }

    /// <summary>
    ///     Gets a value indicating whether symbol-aware evidence is required.
    /// </summary>
    public bool RequiresSymbolEvidence { get; }
}

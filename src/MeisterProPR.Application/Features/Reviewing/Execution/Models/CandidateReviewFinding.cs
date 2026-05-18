// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Structured final candidate finding evaluated by the deterministic final gate.
/// </summary>
public sealed record CandidateReviewFinding
{
    /// <summary>
    ///     Category used for findings tied to a specific file-level review comment.
    /// </summary>
    public const string PerFileCommentCategory = "per_file_comment";

    /// <summary>
    ///     Category used for findings that span the overall review rather than a single file.
    /// </summary>
    public const string CrossCuttingCategory = "cross_cutting";

    /// <summary>
    ///     Invariant-check context key for the primary claim kind.
    /// </summary>
    public const string ClaimKindContextKey = "claimKind";

    /// <summary>
    ///     Invariant-check context key for the primary claim identifier.
    /// </summary>
    public const string ClaimIdContextKey = "claimId";

    /// <summary>
    ///     Invariant-check context key for the primary claim family.
    /// </summary>
    public const string ClaimFamilyContextKey = "claimFamily";

    /// <summary>
    ///     Invariant-check context key for the total number of claims in scope.
    /// </summary>
    public const string ClaimCountContextKey = "claimCount";

    /// <summary>
    ///     Invariant-check context key for the trigger family that justified bounded follow-up.
    /// </summary>
    public const string FollowUpTriggerFamilyContextKey = "followUpTriggerFamily";

    /// <summary>
    ///     Invariant-check context key for repeated-judgment agreement state.
    /// </summary>
    public const string RepeatedJudgmentAgreementStateContextKey = "repeatedJudgmentAgreementState";

    /// <summary>
    ///     Invariant-check context key for repeated-judgment evidence-set reuse.
    /// </summary>
    public const string RepeatedJudgmentUsedSameEvidenceSetContextKey = "repeatedJudgmentUsedSameEvidenceSet";

    /// <summary>
    ///     Invariant-check context key for repeated-judgment source support.
    /// </summary>
    public const string RepeatedJudgmentSupportSourceContextKey = "repeatedJudgmentSupportSource";

    /// <summary>
    ///     Claim kind used when a review comment message is unexpectedly null.
    /// </summary>
    public const string ReviewCommentMessageNullableClaimKind = "review_comment_message_nullable";

    /// <summary>
    ///     Claim kind used when review result comments are unexpectedly null.
    /// </summary>
    public const string ReviewResultCommentsNullableClaimKind = "review_result_comments_nullable";

    /// <summary>
    ///     Claim kind used when duplicate per-file results are expected.
    /// </summary>
    public const string ReviewFileResultsDuplicateExpectedClaimKind = "review_file_results_duplicate_expected";

    /// <summary>
    ///     Claim kind used when cross-file evidence is required.
    /// </summary>
    public const string CrossFileEvidenceRequiredClaimKind = "cross_file_evidence_required";

    /// <summary>
    ///     Claim kind used for generic review assertions.
    /// </summary>
    public const string GenericReviewAssertionClaimKind = "generic_review_assertion";

    /// <summary>
    ///     Claim kind used for Docker final-stage root-user runtime findings.
    /// </summary>
    public const string DockerFinalStageRootUserClaimKind = "docker_final_stage_root_user";

    /// <summary>
    ///     Claim kind used for GitHub Actions secret echo or logging exposure findings.
    /// </summary>
    public const string GitHubActionsSecretEchoClaimKind = "github_actions_secret_echo";

    /// <summary>
    ///     Claim kind used for Terraform public-ingress exposure findings.
    /// </summary>
    public const string TerraformPublicIngressClaimKind = "terraform_public_ingress";

    /// <summary>
    ///     Claim kind used for manifest and lockfile misalignment findings.
    /// </summary>
    public const string ManifestLockfileMisalignmentClaimKind = "manifest_lockfile_misalignment";

    /// <summary>
    ///     Claim kind used for missing wiring or dispatch registration findings.
    /// </summary>
    public const string WiringMissingRegistrationClaimKind = "wiring_missing_registration";

    /// <summary>
    ///     Claim kind used for shell unquoted variable expansion findings.
    /// </summary>
    public const string ShellUnquotedVariableClaimKind = "shell_unquoted_variable";

    /// <summary>
    ///     Initializes a final-gated candidate finding.
    /// </summary>
    /// <param name="findingId">Stable identifier for the finding.</param>
    /// <param name="provenance">Source details that explain how the finding was produced.</param>
    /// <param name="severity">Severity assigned to the finding.</param>
    /// <param name="message">Human-readable finding message.</param>
    /// <param name="category">Category describing the finding scope.</param>
    /// <param name="filePath">Optional repository-relative file path for the finding anchor.</param>
    /// <param name="lineNumber">Optional line number for the finding anchor.</param>
    /// <param name="evidence">Optional evidence snapshot supporting the finding.</param>
    /// <param name="candidateSummaryText">Optional candidate summary produced before final gating.</param>
    /// <param name="invariantCheckContext">Optional invariant metadata captured during validation.</param>
    /// <param name="verificationOutcome">Optional verification result associated with the finding.</param>
    public CandidateReviewFinding(
        string findingId,
        CandidateFindingProvenance provenance,
        CommentSeverity severity,
        string message,
        string category,
        string? filePath = null,
        int? lineNumber = null,
        EvidenceReference? evidence = null,
        string? candidateSummaryText = null,
        IReadOnlyDictionary<string, string>? invariantCheckContext = null,
        VerificationOutcome? verificationOutcome = null)
    {
        if (string.IsNullOrWhiteSpace(findingId))
        {
            throw new ArgumentException("Finding ID is required.", nameof(findingId));
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("Message is required.", nameof(message));
        }

        if (string.IsNullOrWhiteSpace(category))
        {
            throw new ArgumentException("Category is required.", nameof(category));
        }

        this.FindingId = findingId;
        this.Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        this.Severity = severity;
        this.Message = message;
        this.Category = category;
        this.FilePath = filePath;
        this.LineNumber = lineNumber;
        this.Evidence = evidence;
        this.CandidateSummaryText = candidateSummaryText;
        this.InvariantCheckContext = invariantCheckContext is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(invariantCheckContext, StringComparer.Ordinal);
        this.VerificationOutcome = verificationOutcome;
    }

    /// <summary>
    ///     Gets the stable identifier for the finding.
    /// </summary>
    public string FindingId { get; }

    /// <summary>
    ///     Gets the provenance describing how the finding was generated.
    /// </summary>
    public CandidateFindingProvenance Provenance { get; }

    /// <summary>
    ///     Gets the severity assigned to the finding.
    /// </summary>
    public CommentSeverity Severity { get; }

    /// <summary>
    ///     Gets the human-readable finding message.
    /// </summary>
    public string Message { get; }

    /// <summary>
    ///     Gets the category describing the finding scope.
    /// </summary>
    public string Category { get; }

    /// <summary>
    ///     Gets the repository-relative file path for the finding anchor when available.
    /// </summary>
    public string? FilePath { get; }

    /// <summary>
    ///     Gets the line number for the finding anchor when available.
    /// </summary>
    public int? LineNumber { get; }

    /// <summary>
    ///     Gets the evidence snapshot supporting the finding when available.
    /// </summary>
    public EvidenceReference? Evidence { get; }

    /// <summary>
    ///     Gets the candidate summary text captured before final gating.
    /// </summary>
    public string? CandidateSummaryText { get; }

    /// <summary>
    ///     Gets invariant metadata collected while evaluating the finding.
    /// </summary>
    public IReadOnlyDictionary<string, string> InvariantCheckContext { get; }

    /// <summary>
    ///     Gets the verification result associated with the finding when available.
    /// </summary>
    public VerificationOutcome? VerificationOutcome { get; }

    /// <summary>
    ///     Gets merged-candidate metadata when the finding has been through late-steering merge.
    /// </summary>
    public MergedCandidateFinding? MergedFinding { get; init; }

    /// <summary>
    ///     Creates a new instance of <see cref="CandidateReviewFinding" /> with merged provenance information.
    /// </summary>
    /// <param name="findingProvenanceKind">The kind of provenance for the merged finding.</param>
    /// <param name="sourcePasses">The review passes that contributed to the merged finding.</param>
    /// <param name="mergeDecision">The decision made during the merge process.</param>
    /// <param name="identityKey">The identity key for the merged finding.</param>
    /// <param name="mergeGroupKey">The group key for the merged finding.</param>
    /// <returns>A new instance of <see cref="CandidateReviewFinding" /> with merged provenance information.</returns>
    public CandidateReviewFinding WithMergedProvenance(
        FindingProvenanceKind findingProvenanceKind,
        IReadOnlyList<ReviewPassKind>? sourcePasses = null,
        string? mergeDecision = null,
        string? identityKey = null,
        string? mergeGroupKey = null)
    {
        return this with
        {
            MergedFinding = new MergedCandidateFinding(
                this,
                findingProvenanceKind,
                sourcePasses ?? [this.Provenance.ReviewPassKind],
                identityKey ?? this.FindingId,
                mergeDecision ?? findingProvenanceKind.ToString(),
                mergeGroupKey),
        };
    }

    /// <summary>
    ///     Builds invariant-check context values from the supplied claims.
    /// </summary>
    /// <param name="claims">Claims to summarize into invariant-check metadata.</param>
    /// <returns>A dictionary containing the summarized invariant-check context.</returns>
    public static IReadOnlyDictionary<string, string> CreateInvariantCheckContext(IReadOnlyList<ClaimDescriptor> claims)
    {
        ArgumentNullException.ThrowIfNull(claims);

        if (claims.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        var firstClaim = claims[0];
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ClaimKindContextKey] = firstClaim.ClaimKind,
            [ClaimIdContextKey] = firstClaim.ClaimId,
            [ClaimFamilyContextKey] = firstClaim.ClaimFamily,
            [ClaimCountContextKey] = claims.Count.ToString(CultureInfo.InvariantCulture),
        };
    }
}

public sealed record PassCandidateFinding(
    ReviewPassKind PassKind,
    CandidateReviewFinding Finding,
    string IdentityKey,
    string? MergeGroupKey = null);

public sealed record MergedCandidateFinding(
    CandidateReviewFinding Finding,
    FindingProvenanceKind Provenance,
    IReadOnlyList<ReviewPassKind> SourcePasses,
    string IdentityKey,
    string MergeDecision,
    string? MergeGroupKey = null);

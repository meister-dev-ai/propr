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
    public const string PerFileCommentCategory = "per_file_comment";
    public const string CrossCuttingCategory = "cross_cutting";
    public const string ClaimKindContextKey = "claimKind";
    public const string ClaimIdContextKey = "claimId";
    public const string ClaimFamilyContextKey = "claimFamily";
    public const string ClaimCountContextKey = "claimCount";
    public const string ReviewCommentMessageNullableClaimKind = "review_comment_message_nullable";
    public const string ReviewResultCommentsNullableClaimKind = "review_result_comments_nullable";
    public const string ReviewFileResultsDuplicateExpectedClaimKind = "review_file_results_duplicate_expected";
    public const string CrossFileEvidenceRequiredClaimKind = "cross_file_evidence_required";
    public const string GenericReviewAssertionClaimKind = "generic_review_assertion";

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

    public string FindingId { get; }

    public CandidateFindingProvenance Provenance { get; }

    public CommentSeverity Severity { get; }

    public string Message { get; }

    public string Category { get; }

    public string? FilePath { get; }

    public int? LineNumber { get; }

    public EvidenceReference? Evidence { get; }

    public string? CandidateSummaryText { get; }

    public IReadOnlyDictionary<string, string> InvariantCheckContext { get; }

    public VerificationOutcome? VerificationOutcome { get; }

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

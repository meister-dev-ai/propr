// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Reviewing-owned invariant fact used by deterministic contradiction checks.
/// </summary>
public sealed record InvariantFact
{
    public const string DomainFamily = "domain";
    public const string ArchitectureFamily = "architecture";
    public const string PersistenceFamily = "persistence";
    public const string ReviewCommentMessageRequiredInvariantId = "review_comment_message_required";
    public const string ReviewResultCommentsRequiredInvariantId = "review_result_comments_required";
    public const string ReviewFileResultsUniqueJobPathInvariantId = "review_file_results_unique_job_file_path";

    private static readonly IReadOnlyDictionary<string, string> BlockingInvariantIdsByClaimKind =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CandidateReviewFinding.ReviewCommentMessageNullableClaimKind] = ReviewCommentMessageRequiredInvariantId,
            [CandidateReviewFinding.ReviewResultCommentsNullableClaimKind] = ReviewResultCommentsRequiredInvariantId,
            [CandidateReviewFinding.ReviewFileResultsDuplicateExpectedClaimKind] = ReviewFileResultsUniqueJobPathInvariantId,
        };

    public InvariantFact(
        string invariantId,
        string family,
        string name,
        string source,
        string factValue,
        string description)
    {
        if (string.IsNullOrWhiteSpace(invariantId))
        {
            throw new ArgumentException("Invariant ID is required.", nameof(invariantId));
        }

        if (string.IsNullOrWhiteSpace(family))
        {
            throw new ArgumentException("Invariant family is required.", nameof(family));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Invariant name is required.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("Invariant source is required.", nameof(source));
        }

        if (string.IsNullOrWhiteSpace(factValue))
        {
            throw new ArgumentException("Invariant fact value is required.", nameof(factValue));
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("Invariant description is required.", nameof(description));
        }

        this.InvariantId = invariantId;
        this.Family = family;
        this.Name = name;
        this.Source = source;
        this.FactValue = factValue;
        this.Description = description;
    }

    public string InvariantId { get; }

    public string Family { get; }

    public string Name { get; }

    public string Source { get; }

    public string FactValue { get; }

    public string Description { get; }

    public static bool TryGetBlockingInvariantId(string claimKind, out string invariantId)
    {
        if (string.IsNullOrWhiteSpace(claimKind))
        {
            invariantId = string.Empty;
            return false;
        }

        return BlockingInvariantIdsByClaimKind.TryGetValue(claimKind, out invariantId!);
    }
}

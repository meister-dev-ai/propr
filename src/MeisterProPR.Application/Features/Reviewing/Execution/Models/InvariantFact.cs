// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Reviewing-owned invariant fact used by deterministic contradiction checks.
/// </summary>
public sealed record InvariantFact
{
    /// <summary>
    ///     Invariant family used for domain-level facts.
    /// </summary>
    public const string DomainFamily = "domain";

    /// <summary>
    ///     Invariant family used for architecture-level facts.
    /// </summary>
    public const string ArchitectureFamily = "architecture";

    /// <summary>
    ///     Invariant family used for persistence-level facts.
    /// </summary>
    public const string PersistenceFamily = "persistence";

    /// <summary>
    ///     Invariant identifier requiring review comment messages to be present.
    /// </summary>
    public const string ReviewCommentMessageRequiredInvariantId = "review_comment_message_required";

    /// <summary>
    ///     Invariant identifier requiring review result comments to be present.
    /// </summary>
    public const string ReviewResultCommentsRequiredInvariantId = "review_result_comments_required";

    /// <summary>
    ///     Invariant identifier requiring unique review file-result job paths.
    /// </summary>
    public const string ReviewFileResultsUniqueJobPathInvariantId = "review_file_results_unique_job_file_path";

    private static readonly IReadOnlyDictionary<string, string> BlockingInvariantIdsByClaimKind =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CandidateReviewFinding.ReviewCommentMessageNullableClaimKind] = ReviewCommentMessageRequiredInvariantId,
            [CandidateReviewFinding.ReviewResultCommentsNullableClaimKind] = ReviewResultCommentsRequiredInvariantId,
            [CandidateReviewFinding.ReviewFileResultsDuplicateExpectedClaimKind] = ReviewFileResultsUniqueJobPathInvariantId,
        };

    /// <summary>
    ///     Initializes a deterministic invariant fact.
    /// </summary>
    /// <param name="invariantId">Stable identifier for the invariant.</param>
    /// <param name="family">Family grouping for the invariant.</param>
    /// <param name="name">Human-readable invariant name.</param>
    /// <param name="source">Source that supplied the invariant fact.</param>
    /// <param name="factValue">Normalized fact value.</param>
    /// <param name="description">Human-readable description of the invariant.</param>
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

    /// <summary>
    ///     Gets the stable identifier for the invariant.
    /// </summary>
    public string InvariantId { get; }

    /// <summary>
    ///     Gets the family grouping for the invariant.
    /// </summary>
    public string Family { get; }

    /// <summary>
    ///     Gets the human-readable invariant name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    ///     Gets the source that supplied the invariant fact.
    /// </summary>
    public string Source { get; }

    /// <summary>
    ///     Gets the normalized fact value.
    /// </summary>
    public string FactValue { get; }

    /// <summary>
    ///     Gets the human-readable description of the invariant.
    /// </summary>
    public string Description { get; }

    /// <summary>
    ///     Resolves the blocking invariant associated with a claim kind when one exists.
    /// </summary>
    /// <param name="claimKind">Claim kind to resolve.</param>
    /// <param name="invariantId">Resolved blocking invariant identifier.</param>
    /// <returns><c>true</c> when a blocking invariant exists; otherwise <c>false</c>.</returns>
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

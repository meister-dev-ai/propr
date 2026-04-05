// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>
///     Aggregated result of a review run, including summary and comments.
/// </summary>
/// <param name="Summary">Overall summary of the review results.</param>
/// <param name="Comments">List of individual review comments.</param>
public sealed record ReviewResult(
    string Summary,
    IReadOnlyList<ReviewComment> Comments)
{
    /// <summary>
    ///     File paths whose results were carried forward from a prior iteration's review.
    ///     Empty for full (non-incremental) reviews.
    /// </summary>
    public IReadOnlyList<string> CarriedForwardFilePaths { get; init; } = [];

    /// <summary>
    ///     Count of comment candidates suppressed before posting because they originated from
    ///     carried-forward file results rather than a fresh review pass.
    /// </summary>
    public int CarriedForwardCandidatesSkipped { get; init; }
}

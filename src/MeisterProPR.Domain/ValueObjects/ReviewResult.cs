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

    /// <summary>
    ///     File paths reviewed diff-only because their full context exceeded the model's context window.
    ///     Surfaced in the review summary so a degraded review is never silently presented as a full one.
    /// </summary>
    public IReadOnlyList<string> ContextDegradedFilePaths { get; init; } = [];

    /// <summary>
    ///     File paths skipped without a review because even their minimal payload exceeded the model's
    ///     context window. Surfaced in the review summary so a skipped file is visible rather than missing.
    /// </summary>
    public IReadOnlyList<string> ContextSkippedFilePaths { get; init; } = [];

    /// <summary>
    ///     True when the review stopped scanning further files because the per-increment budget soft cap was
    ///     reached mid-run. The review still completed with a synthesis over the files that were reviewed.
    /// </summary>
    public bool BudgetSoftCapped { get; init; }

    /// <summary>The per-increment soft cap (USD) that was reached, when <see cref="BudgetSoftCapped" /> is true.</summary>
    public decimal? BudgetSoftCapThresholdUsd { get; init; }

    /// <summary>The metered spend (USD) that reached the soft cap, when <see cref="BudgetSoftCapped" /> is true.</summary>
    public decimal? BudgetSoftCapSpentUsd { get; init; }

    /// <summary>
    ///     File paths not scanned because the per-increment budget soft cap was reached mid-run. Surfaced in the
    ///     review summary so a budget-shortened review is never silently presented as a full one.
    /// </summary>
    public IReadOnlyList<string> BudgetSoftCapSkippedFilePaths { get; init; } = [];
}

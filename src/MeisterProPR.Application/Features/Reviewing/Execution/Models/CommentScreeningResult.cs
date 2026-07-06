// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Semantic screening class for a review comment. Language-agnostic by construction: derived from embedding
///     similarity to labeled exemplars, not from phrase matching, so the same class is assigned to
///     semantically-equivalent phrasing in any language.
/// </summary>
public enum CommentScreeningClass
{
    /// <summary>A concrete, firm finding — keep it as-is.</summary>
    Firm,

    /// <summary>
    ///     Speculative / hedged phrasing (uncertain, "might", "please verify"). Disposition depends on severity —
    ///     ERROR/WARNING route to evidence verification, SUGGESTION demotes to summary-only.
    /// </summary>
    Hedged,

    /// <summary>A vague, non-actionable suggestion — demote to summary-only.</summary>
    Vague,
}

/// <summary>
///     Result of semantic comment screening: the classified <see cref="CommentScreeningClass" />, the cosine
///     similarity (0–1) to the winning class centroid, and whether the classification was degraded (screening
///     unavailable — no embedding model bound, or a failure). A degraded result is always
///     <see cref="CommentScreeningClass.Firm" /> so the comment is kept, but the flag lets the caller record a
///     single <c>screening_degraded</c> trace instead of silently disabling screening for the file.
/// </summary>
public sealed record CommentScreeningResult(CommentScreeningClass Class, double Similarity, bool IsDegraded = false)
{
    /// <summary>The conservative "keep the comment" result for a genuine firm classification (or blank input).</summary>
    public static CommentScreeningResult Firm { get; } = new(CommentScreeningClass.Firm, 0.0);

    /// <summary>The conservative "keep the comment" result used when screening is unavailable (degraded path).</summary>
    public static CommentScreeningResult DegradedFirm { get; } = new(CommentScreeningClass.Firm, 0.0, true);
}

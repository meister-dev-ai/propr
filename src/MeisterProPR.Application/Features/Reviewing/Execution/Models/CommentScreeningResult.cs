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
///     Result of semantic comment screening: the classified <see cref="CommentScreeningClass" /> and the cosine
///     similarity (0–1) to the winning class centroid. <see cref="Firm" /> with similarity 0 is the conservative
///     degraded result returned when no embedding model is bound or the screener could not decide — the comment
///     is kept, never dropped on a screening error.
/// </summary>
public sealed record CommentScreeningResult(CommentScreeningClass Class, double Similarity)
{
    /// <summary>The conservative "keep the comment" result used on the degraded path.</summary>
    public static CommentScreeningResult Firm { get; } = new(CommentScreeningClass.Firm, 0.0);
}

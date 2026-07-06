// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Classifies a review comment as firm / hedged / vague by semantic similarity to labeled exemplars, using
///     the client's embedding model. This is the language-robust replacement for the English phrase-list filters:
///     a multilingual embedding maps semantically-equivalent hedging across languages to the same class, so no
///     per-language phrase list is needed. Degraded-safe — an implementation that cannot decide (no embedding
///     model bound, or a failure) returns <see cref="CommentScreeningClass.Firm" />, so a comment is kept, never
///     dropped on a screening error.
/// </summary>
public interface ISemanticCommentScreener
{
    /// <summary>
    ///     Classifies <paramref name="commentText" /> for the given client. Returns
    ///     <see cref="CommentScreeningResult.Firm" /> on any degraded path (missing model / failure / blank text).
    /// </summary>
    Task<CommentScreeningResult> ClassifyAsync(string commentText, Guid clientId, CancellationToken ct = default);
}

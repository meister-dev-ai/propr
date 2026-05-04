// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Reviewing-owned boundary for adjudicating ambiguous comment-relevance decisions.
/// </summary>
public interface ICommentRelevanceAmbiguityEvaluator
{
    /// <summary>
    ///     Evaluates only the ambiguous survivors from a single file-level relevance pass.
    /// </summary>
    /// <param name="request">Normalized filter input for the current file.</param>
    /// <param name="comments">The ambiguous comments to adjudicate.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<CommentRelevanceAmbiguityEvaluationResult> EvaluateAsync(
        CommentRelevanceFilterRequest request,
        IReadOnlyList<ReviewComment> comments,
        CancellationToken ct = default);
}

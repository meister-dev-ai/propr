// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.CommentRelevance;

internal sealed class PassThroughCommentRelevanceAmbiguityEvaluator : ICommentRelevanceAmbiguityEvaluator
{
    private const string EvaluatorDegradedComponent = "comment_relevance_evaluator";
    private const string AmbiguousSurvivorFallbackCheck = "ambiguous_survivors_left_unchanged";

    public Task<CommentRelevanceAmbiguityEvaluationResult> EvaluateAsync(
        CommentRelevanceFilterRequest request,
        IReadOnlyList<ReviewComment> comments,
        CancellationToken ct = default)
    {
        return Task.FromResult(
            CommentRelevanceAmbiguityEvaluationResult.Unavailable(
                [EvaluatorDegradedComponent],
                [AmbiguousSurvivorFallbackCheck],
                "Comment relevance evaluator is unavailable; ambiguous survivors were kept unchanged."));
    }
}

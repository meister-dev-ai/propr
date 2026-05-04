// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.CommentRelevance;

internal sealed class HybridCommentRelevanceFilter(ICommentRelevanceAmbiguityEvaluator ambiguityEvaluator) : ICommentRelevanceFilter
{
    public string ImplementationId => "hybrid-v1";

    public string ImplementationVersion => "1.0.0";

    public async Task<CommentRelevanceFilterResult> FilterAsync(
        CommentRelevanceFilterRequest request,
        CancellationToken ct = default)
    {
        var decisions = request.Comments
            .Select(comment => HeuristicCommentRelevanceFilter.EvaluateComment(request, comment, true))
            .ToArray();

        var ambiguousIndexes = decisions
            .Select((decision, index) => (decision, index))
            .Where(item => item.decision.IsKeep && item.decision.ReasonCodes.Count > 0)
            .Select(item => item.index)
            .ToArray();

        if (ambiguousIndexes.Length == 0)
        {
            HeuristicCommentRelevanceFilter.ApplyDuplicateLocalPattern(decisions);
            return new CommentRelevanceFilterResult(
                this.ImplementationId,
                this.ImplementationVersion,
                request.FilePath,
                request.Comments.Count,
                decisions.ToList().AsReadOnly());
        }

        var ambiguousComments = ambiguousIndexes.Select(index => request.Comments[index]).ToList().AsReadOnly();
        var evaluation = await ambiguityEvaluator.EvaluateAsync(request, ambiguousComments, ct);
        if (!evaluation.IsTrustworthy || evaluation.Decisions.Count != ambiguousIndexes.Length)
        {
            var degradedComponents = evaluation.DegradedComponents.Count > 0
                ? evaluation.DegradedComponents
                : ["comment_relevance_evaluator"];
            var fallbackChecks = evaluation.FallbackChecks.Count > 0
                ? evaluation.FallbackChecks
                : ["ambiguous_survivors_left_unchanged"];
            var degradedCause = !string.IsNullOrWhiteSpace(evaluation.DegradedCause)
                ? evaluation.DegradedCause
                : evaluation.Decisions.Count != ambiguousIndexes.Length
                    ? "Comment relevance evaluator returned an incomplete decision set."
                    : "Comment relevance evaluator was not trustworthy; ambiguous survivors were kept unchanged.";

            var fallbackDecisions = decisions.ToArray();
            foreach (var index in ambiguousIndexes)
            {
                fallbackDecisions[index] = new CommentRelevanceFilterDecision(
                    CommentRelevanceFilterDecision.KeepDecision,
                    decisions[index].OriginalComment,
                    [],
                    CommentRelevanceFilterDecision.FallbackModeSource);
            }

            HeuristicCommentRelevanceFilter.ApplyDuplicateLocalPattern(fallbackDecisions);

            return new CommentRelevanceFilterResult(
                this.ImplementationId,
                this.ImplementationVersion,
                request.FilePath,
                request.Comments.Count,
                fallbackDecisions.ToList().AsReadOnly(),
                degradedComponents,
                fallbackChecks,
                degradedCause,
                evaluation.AiTokenUsage);
        }

        for (var i = 0; i < ambiguousIndexes.Length; i++)
        {
            decisions[ambiguousIndexes[i]] = evaluation.Decisions[i];
        }

        HeuristicCommentRelevanceFilter.ApplyDuplicateLocalPattern(decisions);

        return new CommentRelevanceFilterResult(
            this.ImplementationId,
            this.ImplementationVersion,
            request.FilePath,
            request.Comments.Count,
            decisions.ToList().AsReadOnly(),
            evaluation.DegradedComponents,
            evaluation.FallbackChecks,
            evaluation.DegradedCause,
            evaluation.AiTokenUsage);
    }
}

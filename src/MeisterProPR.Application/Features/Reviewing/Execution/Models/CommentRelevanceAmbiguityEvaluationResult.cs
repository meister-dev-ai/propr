// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Result returned by a comment relevance ambiguity evaluator for ambiguous survivor comments.
/// </summary>
public sealed record CommentRelevanceAmbiguityEvaluationResult(
    IReadOnlyList<CommentRelevanceFilterDecision> Decisions,
    bool IsTrustworthy,
    FilterAiTokenUsage? AiTokenUsage,
    IReadOnlyList<string> DegradedComponents,
    IReadOnlyList<string> FallbackChecks,
    string? DegradedCause)
{
    /// <summary>
    ///     Returns an unavailable or degraded evaluator result.
    /// </summary>
    public static CommentRelevanceAmbiguityEvaluationResult Unavailable(
        IReadOnlyList<string>? degradedComponents,
        IReadOnlyList<string>? fallbackChecks,
        string? degradedCause)
    {
        return new CommentRelevanceAmbiguityEvaluationResult(
            [],
            false,
            null,
            degradedComponents ?? [],
            fallbackChecks ?? [],
            degradedCause);
    }
}

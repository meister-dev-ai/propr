// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.CommentRelevance;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.CommentRelevance;

public sealed class HybridCommentRelevanceFilterTests
{
    [Fact]
    public async Task FilterAsync_WhenEvaluatorUnavailable_AppliesDeterministicDiscardsAndKeepsAmbiguousSurvivors()
    {
        var evaluator = Substitute.For<ICommentRelevanceAmbiguityEvaluator>();
        evaluator.EvaluateAsync(Arg.Any<CommentRelevanceFilterRequest>(), Arg.Any<IReadOnlyList<ReviewComment>>(), Arg.Any<CancellationToken>())
            .Returns(
                CommentRelevanceAmbiguityEvaluationResult.Unavailable(
                    ["comment_relevance_evaluator"],
                    ["ambiguous_survivors_left_unchanged"],
                    "Evaluator unavailable."));

        var filter = new HybridCommentRelevanceFilter(evaluator);
        var deterministicDiscard = CommentRelevanceFilterTestData.CreateComment("Overall this file has several issues across multiple places.");
        var ambiguous = CommentRelevanceFilterTestData.CreateComment("Critical issue may exist in another file.");

        var result = await filter.FilterAsync(
            CommentRelevanceFilterTestData.CreateRequest([deterministicDiscard, ambiguous], "hybrid-v1"),
            CancellationToken.None);

        Assert.Equal(1, result.KeptCount);
        Assert.Equal(1, result.DiscardedCount);
        AssertFallbackState(result, "Evaluator unavailable.");
        Assert.Equal(CommentRelevanceFilterDecision.FallbackModeSource, result.Decisions.Single(decision => decision.IsKeep).DecisionSource);
        Assert.Contains(CommentRelevanceReasonCodes.SummaryLevelOnly, result.Decisions.Single(d => d.IsDiscard).ReasonCodes);
    }

    [Fact]
    public async Task FilterAsync_WhenEvaluatorParseFails_KeepsAmbiguousSurvivorsInFallbackMode()
    {
        var result = await RunFallbackScenarioAsync("Comment relevance evaluator returned an unparseable response.");

        Assert.Single(result.GetKeptComments());
        AssertFallbackState(result, "Comment relevance evaluator returned an unparseable response.");
        Assert.All(
            result.Decisions.Where(decision => decision.IsKeep),
            decision =>
                Assert.Equal(CommentRelevanceFilterDecision.FallbackModeSource, decision.DecisionSource));
    }

    [Fact]
    public async Task FilterAsync_WhenEvaluatorTimesOut_KeepsAmbiguousSurvivorsInFallbackMode()
    {
        var result = await RunFallbackScenarioAsync("Comment relevance evaluator timed out.");

        Assert.Single(result.GetKeptComments());
        AssertFallbackState(result, "Comment relevance evaluator timed out.");
    }

    [Fact]
    public async Task FilterAsync_WhenAmbiguousSelectionFails_RecordsDegradedFallbackAndKeepsAmbiguousSurvivors()
    {
        var evaluator = Substitute.For<ICommentRelevanceAmbiguityEvaluator>();
        evaluator.EvaluateAsync(Arg.Any<CommentRelevanceFilterRequest>(), Arg.Any<IReadOnlyList<ReviewComment>>(), Arg.Any<CancellationToken>())
            .Returns(
                new CommentRelevanceAmbiguityEvaluationResult(
                    [],
                    true,
                    new FilterAiTokenUsage(
                        "hybrid-v1",
                        CommentRelevanceFilterTestData.DefaultFilePath,
                        44,
                        11,
                        AiConnectionModelCategory.Default,
                        "selection-mismatch"),
                    [],
                    [],
                    null));

        var filter = new HybridCommentRelevanceFilter(evaluator);
        var firstAmbiguous = CommentRelevanceFilterTestData.CreateComment("Critical issue may exist in another file.");
        var secondAmbiguous = CommentRelevanceFilterTestData.CreateComment("behavior is broken in some cases.", lineNumber: null);

        var result = await filter.FilterAsync(
            CommentRelevanceFilterTestData.CreateRequest([firstAmbiguous, secondAmbiguous], "hybrid-v1"),
            CancellationToken.None);

        Assert.Equal(2, result.KeptCount);
        Assert.Equal(0, result.DiscardedCount);
        Assert.NotNull(result.AiTokenUsage);
        Assert.Equal(44, result.AiTokenUsage!.InputTokens);
        AssertFallbackState(result, "Comment relevance evaluator returned an incomplete decision set.");
        Assert.All(result.Decisions, decision => Assert.Equal(CommentRelevanceFilterDecision.FallbackModeSource, decision.DecisionSource));
    }

    [Fact]
    public async Task FilterAsync_WhenEvaluatorOutageAffectsAllComments_FallsBackForEntireFile()
    {
        var evaluator = Substitute.For<ICommentRelevanceAmbiguityEvaluator>();
        evaluator.EvaluateAsync(Arg.Any<CommentRelevanceFilterRequest>(), Arg.Any<IReadOnlyList<ReviewComment>>(), Arg.Any<CancellationToken>())
            .Returns(
                CommentRelevanceAmbiguityEvaluationResult.Unavailable(
                    ["comment_relevance_evaluator"],
                    ["ambiguous_survivors_left_unchanged"],
                    "Comment relevance evaluator failed; ambiguous survivors were kept unchanged."));

        var filter = new HybridCommentRelevanceFilter(evaluator);
        var comments = new[]
        {
            CommentRelevanceFilterTestData.CreateComment("Critical issue may exist in another file."),
            CommentRelevanceFilterTestData.CreateComment("behavior is broken in some cases."),
        };

        var result = await filter.FilterAsync(
            CommentRelevanceFilterTestData.CreateRequest(comments, "hybrid-v1"),
            CancellationToken.None);

        Assert.Equal(2, result.KeptCount);
        Assert.Equal(0, result.DiscardedCount);
        AssertFallbackState(result, "Comment relevance evaluator failed; ambiguous survivors were kept unchanged.");
        Assert.All(result.Decisions, decision => Assert.Equal(CommentRelevanceFilterDecision.FallbackModeSource, decision.DecisionSource));
    }

    [Fact]
    public async Task FilterAsync_WhenEvaluatorReturnsDecisions_UsesAiAdjudicationAndTokenUsage()
    {
        var evaluator = Substitute.For<ICommentRelevanceAmbiguityEvaluator>();
        var ambiguousComment = CommentRelevanceFilterTestData.CreateComment("Critical issue may exist in another file.");
        evaluator.EvaluateAsync(Arg.Any<CommentRelevanceFilterRequest>(), Arg.Any<IReadOnlyList<ReviewComment>>(), Arg.Any<CancellationToken>())
            .Returns(
                new CommentRelevanceAmbiguityEvaluationResult(
                    [
                        new CommentRelevanceFilterDecision(
                            CommentRelevanceFilterDecision.DiscardDecision,
                            ambiguousComment,
                            [CommentRelevanceReasonCodes.UnverifiableCrossFileClaim],
                            CommentRelevanceFilterDecision.AiAdjudicationSource),
                    ],
                    true,
                    new FilterAiTokenUsage("hybrid-v1", "src/Foo.cs", 120, 24, AiConnectionModelCategory.Default, "test-evaluator"),
                    [],
                    [],
                    null));

        var filter = new HybridCommentRelevanceFilter(evaluator);
        var result = await filter.FilterAsync(
            CommentRelevanceFilterTestData.CreateRequest([ambiguousComment], "hybrid-v1"),
            CancellationToken.None);

        Assert.Equal(0, result.KeptCount);
        Assert.Equal(1, result.DiscardedCount);
        Assert.NotNull(result.AiTokenUsage);
        Assert.Equal(120, result.AiTokenUsage!.InputTokens);
        Assert.Equal(CommentRelevanceFilterDecision.AiAdjudicationSource, result.ToRecordedOutput().Discarded[0].DecisionSource);
    }

    private static async Task<CommentRelevanceFilterResult> RunFallbackScenarioAsync(string degradedCause)
    {
        var evaluator = Substitute.For<ICommentRelevanceAmbiguityEvaluator>();
        evaluator.EvaluateAsync(Arg.Any<CommentRelevanceFilterRequest>(), Arg.Any<IReadOnlyList<ReviewComment>>(), Arg.Any<CancellationToken>())
            .Returns(
                CommentRelevanceAmbiguityEvaluationResult.Unavailable(
                    ["comment_relevance_evaluator"],
                    ["ambiguous_survivors_left_unchanged"],
                    degradedCause));

        var filter = new HybridCommentRelevanceFilter(evaluator);
        var deterministicDiscard = CommentRelevanceFilterTestData.CreateComment("Overall this file has several issues across multiple places.");
        var ambiguous = CommentRelevanceFilterTestData.CreateComment("Critical issue may exist in another file.");

        return await filter.FilterAsync(
            CommentRelevanceFilterTestData.CreateRequest([deterministicDiscard, ambiguous], "hybrid-v1"),
            CancellationToken.None);
    }

    private static void AssertFallbackState(CommentRelevanceFilterResult result, string expectedCause)
    {
        Assert.Contains("comment_relevance_evaluator", result.DegradedComponents);
        Assert.Contains("ambiguous_survivors_left_unchanged", result.FallbackChecks);
        Assert.Equal(expectedCause, result.DegradedCause);
    }
}

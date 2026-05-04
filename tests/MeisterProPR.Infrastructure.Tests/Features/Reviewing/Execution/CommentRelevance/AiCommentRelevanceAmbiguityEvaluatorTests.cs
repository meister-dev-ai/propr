// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.CommentRelevance;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.CommentRelevance;

public sealed class AiCommentRelevanceAmbiguityEvaluatorTests
{
    [Fact]
    public async Task EvaluateAsync_UsesDefaultReviewRuntimeFromContext()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        """
                        {"decisions":[{"index":0,"decision":"Discard","reasonCodes":["unverifiable_cross_file_claim"]}]}
                        """))
                {
                    Usage = new UsageDetails { InputTokenCount = 101, OutputTokenCount = 17 },
                });

        var request = CommentRelevanceFilterTestData.CreateRequest(
            implementationId: "hybrid-v1",
            comments:
            [
                CommentRelevanceFilterTestData.CreateComment(
                    "Critical issue may exist in another file.",
                    lineNumber: 12),
            ]);
        request.ReviewContext.DefaultReviewChatClient = chatClient;
        request.ReviewContext.DefaultReviewModelId = "client-default-review-model";

        var sut = new AiCommentRelevanceAmbiguityEvaluator(Substitute.For<ILogger<AiCommentRelevanceAmbiguityEvaluator>>());
        var comments = new[] { CommentRelevanceFilterTestData.CreateComment("Critical issue may exist in another file.", lineNumber: 12) };

        var result = await sut.EvaluateAsync(request, comments, CancellationToken.None);

        Assert.True(result.IsTrustworthy);
        Assert.NotNull(result.AiTokenUsage);
        Assert.Equal("client-default-review-model", result.AiTokenUsage!.ModelId);
        Assert.Equal(101, result.AiTokenUsage.InputTokens);
        Assert.Equal(17, result.AiTokenUsage.OutputTokens);
        Assert.Single(result.Decisions);
        Assert.Equal(CommentRelevanceFilterDecision.DiscardDecision, result.Decisions[0].Decision);

        await chatClient.Received(1)
            .GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Is<ChatOptions?>(options => options!.ModelId == "client-default-review-model"),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateAsync_WithoutDefaultReviewRuntime_DegradesCleanly()
    {
        var request = new CommentRelevanceFilterRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "hybrid-v1",
            CommentRelevanceFilterTestData.DefaultFilePath,
            CommentRelevanceFilterTestData.CreateFile(),
            CommentRelevanceFilterTestData.CreatePullRequest(),
            [],
            new ReviewSystemContext(null, [], null),
            Guid.NewGuid());

        var sut = new AiCommentRelevanceAmbiguityEvaluator(Substitute.For<ILogger<AiCommentRelevanceAmbiguityEvaluator>>());

        var result = await sut.EvaluateAsync(
            request,
            [CommentRelevanceFilterTestData.CreateComment("Critical issue may exist in another file.", lineNumber: 12)],
            CancellationToken.None);

        Assert.False(result.IsTrustworthy);
        Assert.Contains("comment_relevance_evaluator", result.DegradedComponents);
        Assert.Equal(
            "Comment relevance evaluator default review runtime is unavailable; ambiguous survivors were kept unchanged.",
            result.DegradedCause);
    }

    [Fact]
    public async Task EvaluateAsync_WhenChatClientIsCanceled_PropagatesCancellation()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns<Task<ChatResponse>>(_ => throw new OperationCanceledException());

        var request = CommentRelevanceFilterTestData.CreateRequest(
            implementationId: "hybrid-v1",
            comments:
            [
                CommentRelevanceFilterTestData.CreateComment(
                    "Critical issue may exist in another file.",
                    lineNumber: 12),
            ]);
        request.ReviewContext.DefaultReviewChatClient = chatClient;
        request.ReviewContext.DefaultReviewModelId = "client-default-review-model";

        var sut = new AiCommentRelevanceAmbiguityEvaluator(Substitute.For<ILogger<AiCommentRelevanceAmbiguityEvaluator>>());

        await Assert.ThrowsAsync<OperationCanceledException>(() => sut.EvaluateAsync(request, request.Comments, CancellationToken.None));
    }

    [Fact]
    public void BuildPrompt_ListsCommentIndexesAndFileContext()
    {
        var prompt = AiCommentRelevanceAmbiguityEvaluator.BuildPrompt(
            CommentRelevanceFilterTestData.CreateRequest(implementationId: "hybrid-v1"),
            [CommentRelevanceFilterTestData.CreateComment("Critical issue may exist in another file.", lineNumber: 12)]);

        Assert.Contains("File: src/Foo.cs", prompt, StringComparison.Ordinal);
        Assert.Contains("[0] severity=warning line=12", prompt, StringComparison.Ordinal);
        Assert.Contains("Critical issue may exist in another file.", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseResponse_ValidJson_ReturnsAiAdjudicationDecision()
    {
        const string json = """
                            {
                              "decisions": [
                                { "index": 0, "decision": "Discard", "reasonCodes": ["unverifiable_cross_file_claim"] }
                              ]
                            }
                            """;

        var decisions = AiCommentRelevanceAmbiguityEvaluator.ParseResponse(
            json,
            [CommentRelevanceFilterTestData.CreateComment("Critical issue may exist in another file.", lineNumber: 12)]);

        Assert.NotNull(decisions);
        Assert.Single(decisions!);
        Assert.Equal(CommentRelevanceFilterDecision.DiscardDecision, decisions[0].Decision);
        Assert.Equal(CommentRelevanceFilterDecision.AiAdjudicationSource, decisions[0].DecisionSource);
        Assert.Equal(CommentRelevanceReasonCodes.UnverifiableCrossFileClaim, decisions[0].ReasonCodes[0]);
    }

    [Fact]
    public void ParseResponse_InvalidShape_ReturnsNull()
    {
        var decisions = AiCommentRelevanceAmbiguityEvaluator.ParseResponse(
            "{\"decisions\":[]}",
            [CommentRelevanceFilterTestData.CreateComment("Critical issue may exist in another file.", lineNumber: 12)]);

        Assert.Null(decisions);
    }
}

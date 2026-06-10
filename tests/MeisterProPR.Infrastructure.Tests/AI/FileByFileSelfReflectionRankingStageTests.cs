// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>
///     Unit tests for <see cref="FileByFileSelfReflectionRankingStage" /> (T106 a–f).
/// </summary>
public sealed class FileByFileSelfReflectionRankingStageTests
{
    private static AiReviewOptions Options(int minScore = 4, int keepTopN = 8)
    {
        return new AiReviewOptions
        {
            ImportanceRankingMinScore = minScore,
            ImportanceRankingKeepTopN = keepTopN,
            ModelId = "test-model",
        };
    }

    private static ReviewComment MakeComment(string message, CommentSeverity severity = CommentSeverity.Warning, int? line = null)
    {
        return new ReviewComment("src/Test.cs", line, severity, message);
    }

    private static ReviewResult MakeResult(IReadOnlyList<ReviewComment> comments)
    {
        return new ReviewResult("summary", comments);
    }

    private static PerFileReviewContext MakeContext(IReadOnlyList<ReviewComment> comments, IChatClient? client = null)
    {
        var reviewContext = new ReviewSystemContext(null, [], null)
        {
            TierChatClient = client,
            ModelId = "test-model",
        };
        var result = MakeResult(comments);
        return new PerFileReviewContext(
            new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com", "p", "r", 1, 1),
            new ChangedFile("src/Test.cs", ChangeType.Edit, "content", "diff"),
            null,
            reviewContext,
            null,
            null,
            result);
    }

    private static string BuildScoreResponse(params (int index, int importance, bool keep)[] scores)
    {
        var items = string.Join(
            ",", scores.Select(s =>
                $"{{\"index\":{s.index},\"importance\":{s.importance},\"keep\":{(s.keep ? "true" : "false")},\"reason\":\"reason\"}}"));
        return $"{{\"scores\":[{items}]}}";
    }

    private FileByFileSelfReflectionRankingStage CreateStage(AiReviewOptions? opts = null)
    {
        return new FileByFileSelfReflectionRankingStage(
            opts ?? Options(),
            Substitute.For<ILogger<FileByFileSelfReflectionRankingStage>>());
    }

    // T106a — 5 candidates, LLM scores [9,2,7,3,8], MinScore=4 → keeps 9/7/8 in score order.
    [Fact]
    public async Task ExecuteAsync_LlmScoresFiveCandidates_KeepsHighScoresInOrder()
    {
        var comments = new[]
        {
            MakeComment("comment index 0"), // score 9 → kept
            MakeComment("comment index 1"), // score 2 → dropped
            MakeComment("comment index 2"), // score 7 → kept
            MakeComment("comment index 3"), // score 3 → dropped
            MakeComment("comment index 4"), // score 8 → kept
        };

        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        BuildScoreResponse((0, 9, true), (1, 2, true), (2, 7, true), (3, 3, true), (4, 8, true)))));

        var context = MakeContext(comments, client);
        var stage = this.CreateStage(Options(4, 10));

        var result = await stage.ExecuteAsync(context, CancellationToken.None);

        Assert.NotNull(result.ReviewResult);
        var kept = result.ReviewResult!.Comments;
        Assert.Equal(3, kept.Count);
        // Order: 9, 8, 7
        Assert.Equal("comment index 0", kept[0].Message);
        Assert.Equal("comment index 4", kept[1].Message);
        Assert.Equal("comment index 2", kept[2].Message);
    }

    // T106b — keeps at most ImportanceRankingKeepTopN.
    [Fact]
    public async Task ExecuteAsync_LlmScoresManyHighCandidates_KeepsAtMostTopN()
    {
        var comments = Enumerable.Range(0, 5)
            .Select(i => MakeComment($"comment {i}"))
            .ToArray();

        var client = Substitute.For<IChatClient>();
        // All score 9
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        BuildScoreResponse((0, 9, true), (1, 9, true), (2, 9, true), (3, 9, true), (4, 9, true)))));

        var context = MakeContext(comments, client);
        var stage = this.CreateStage(Options(4, 3));

        var result = await stage.ExecuteAsync(context, CancellationToken.None);

        Assert.True(result.ReviewResult!.Comments.Count <= 3);
    }

    // T106c — malformed/empty LLM response → falls back to deterministic Score() ordering.
    [Fact]
    public async Task ExecuteAsync_MalformedLlmResponse_FallsBackToDeterministicOrdering()
    {
        var comments = new[]
        {
            MakeComment("security vulnerability found", CommentSeverity.Error),
            MakeComment("minor style issue", CommentSeverity.Suggestion),
            MakeComment("injection risk present"),
        };

        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "NOT VALID JSON {{{")));

        var context = MakeContext(comments, client);
        var stage = this.CreateStage(Options(1, 10));

        var result = await stage.ExecuteAsync(context, CancellationToken.None);

        Assert.NotNull(result.ReviewResult);
        // Fallback deterministic: should still return ranked comments
        Assert.True(result.ReviewResult!.Comments.Count > 0);
    }

    // T106d — 0 or 1 comments → no LLM call.
    [Fact]
    public async Task ExecuteAsync_OneOrZeroComments_NoLlmCall()
    {
        var client = Substitute.For<IChatClient>();

        // 0 comments
        var context0 = MakeContext([], client);
        var stage = this.CreateStage();
        await stage.ExecuteAsync(context0, CancellationToken.None);

        // 1 comment
        var context1 = MakeContext([MakeComment("single")], client);
        await stage.ExecuteAsync(context1, CancellationToken.None);

        await client.DidNotReceive().GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    // T106e — prompt sent to LLM contains each comment's text, severity, deterministic score, and hedging flag.
    [Fact]
    public async Task ExecuteAsync_PromptContainsRequiredFields()
    {
        var comments = new[]
        {
            MakeComment("This may be a null reference issue"), // hedged
            MakeComment("Definite security bypass here at line 42", CommentSeverity.Error), // not hedged
        };

        string? capturedUserMessage = null;
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var messages = callInfo.Arg<IList<ChatMessage>>();
                capturedUserMessage = messages.FirstOrDefault(m => m.Role == ChatRole.User)?.Text;
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, BuildScoreResponse((0, 5, true), (1, 9, true))));
            });

        var context = MakeContext(comments, client);
        var stage = this.CreateStage();

        await stage.ExecuteAsync(context, CancellationToken.None);

        Assert.NotNull(capturedUserMessage);
        // Message text
        Assert.Contains("null reference issue", capturedUserMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("security bypass", capturedUserMessage!, StringComparison.OrdinalIgnoreCase);
        // Severity
        Assert.Contains("warning", capturedUserMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("error", capturedUserMessage!, StringComparison.OrdinalIgnoreCase);
        // Hedging flag
        Assert.Contains("hedging", capturedUserMessage!, StringComparison.OrdinalIgnoreCase);
        // Deterministic score (some numeric value)
        Assert.Matches(@"\d+", capturedUserMessage!);
    }

    // T106f — model id/temperature flow from ReviewSystemContext exactly as QualityFilterExecutor does.
    [Fact]
    public async Task ExecuteAsync_UsesContextModelIdAndTemperature()
    {
        string? observedModelId = null;
        float? observedTemperature = null;
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var options = callInfo.Arg<ChatOptions?>();
                observedModelId = options?.ModelId;
                observedTemperature = options?.Temperature;
                return new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        BuildScoreResponse((0, 8, true), (1, 7, true))));
            });

        var comments = new[]
        {
            MakeComment("first issue", CommentSeverity.Error),
            MakeComment("second issue"),
        };

        var reviewContext = new ReviewSystemContext(null, [], null)
        {
            TierChatClient = client,
            ModelId = "ranking-model-id",
            Temperature = 0.25f,
        };
        var context = new PerFileReviewContext(
            new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com", "p", "r", 1, 1),
            new ChangedFile("src/Test.cs", ChangeType.Edit, "content", "diff"),
            null,
            reviewContext,
            null,
            null,
            MakeResult(comments));

        var stage = this.CreateStage();
        await stage.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal("ranking-model-id", observedModelId);
        Assert.Equal(0.25f, observedTemperature);
    }
}

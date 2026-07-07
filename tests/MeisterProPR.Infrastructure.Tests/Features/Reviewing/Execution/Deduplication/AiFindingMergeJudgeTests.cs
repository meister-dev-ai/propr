// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Deduplication;
using Microsoft.Extensions.AI;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.Deduplication;

/// <summary>
///     The model-backed merge judge is the conservative guard that decides whether two same-file, overlapping-anchor
///     findings are one defect. It must parse the model's JSON verdict (even when wrapped in prose), answer SAME only
///     for an explicit "same" verdict, and be degraded-safe: any missing binding, empty client, unparseable response,
///     or fault returns <see langword="false" /> so distinct bugs are never merged. Cancellation must propagate.
/// </summary>
public sealed class AiFindingMergeJudgeTests
{
    private static readonly Guid Client = Guid.NewGuid();

    [Fact]
    public async Task ReturnsTrue_OnExplicitSameVerdict()
    {
        var judge = JudgeReturning("{\"verdict\":\"same\",\"reason\":\"one underlying bug\"}");

        var result = await judge.AreSameDefectClassAsync(Finding("A"), Finding("B"), Client, CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task ReturnsFalse_OnDifferentVerdict()
    {
        var judge = JudgeReturning("{\"verdict\":\"different\",\"reason\":\"two distinct defects\"}");

        var result = await judge.AreSameDefectClassAsync(Finding("A"), Finding("B"), Client, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task ParsesSameVerdict_WhenJsonIsWrappedInProse()
    {
        // Exercises the first-'{' / last-'}' extraction.
        var judge = JudgeReturning("Sure, here is my judgment: {\"verdict\":\"same\"} — hope that helps.");

        var result = await judge.AreSameDefectClassAsync(Finding("A"), Finding("B"), Client, CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task VerdictMatchIsCaseInsensitive()
    {
        var judge = JudgeReturning("{\"verdict\":\"SAME\"}");

        var result = await judge.AreSameDefectClassAsync(Finding("A"), Finding("B"), Client, CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task ReturnsFalse_WhenResponseHasNoJson()
    {
        var judge = JudgeReturning("Sorry, I can't help with that.");

        var result = await judge.AreSameDefectClassAsync(Finding("A"), Finding("B"), Client, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task ReturnsFalse_WhenVerdictPropertyMissing()
    {
        var judge = JudgeReturning("{\"reason\":\"no verdict field present\"}");

        var result = await judge.AreSameDefectClassAsync(Finding("A"), Finding("B"), Client, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task ReturnsFalse_WhenNoRuntimeResolverIsBound()
    {
        var judge = new AiFindingMergeJudge();

        var result = await judge.AreSameDefectClassAsync(Finding("A"), Finding("B"), Client, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task ReturnsFalse_ForEmptyClientId_WithoutResolving()
    {
        var resolver = Substitute.For<IAiRuntimeResolver>();
        var judge = new AiFindingMergeJudge(resolver);

        var result = await judge.AreSameDefectClassAsync(Finding("A"), Finding("B"), Guid.Empty, CancellationToken.None);

        Assert.False(result);
        await resolver.DidNotReceiveWithAnyArgs().ResolveChatRuntimeAsync(default, default);
    }

    [Fact]
    public async Task ReturnsFalse_WhenResolutionThrows()
    {
        var resolver = Substitute.For<IAiRuntimeResolver>();
        resolver.ResolveChatRuntimeAsync(Arg.Any<Guid>(), AiPurpose.ReviewVerification, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("no active ReviewVerification binding"));
        var judge = new AiFindingMergeJudge(resolver);

        var result = await judge.AreSameDefectClassAsync(Finding("A"), Finding("B"), Client, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task PropagatesCancellation()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());
        var judge = JudgeWith(chatClient);

        await Assert.ThrowsAsync<OperationCanceledException>(() => judge.AreSameDefectClassAsync(Finding("A"), Finding("B"), Client, CancellationToken.None));
    }

    private static AiFindingMergeJudge JudgeReturning(string chatText)
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, chatText)));
        return JudgeWith(chatClient);
    }

    private static AiFindingMergeJudge JudgeWith(IChatClient chatClient)
    {
        var runtime = Substitute.For<IResolvedAiChatRuntime>();
        runtime.ChatClient.Returns(chatClient);
        runtime.Model.Returns(
            new AiConfiguredModelDto(
                Guid.NewGuid(),
                "test-model",
                "Test Model",
                [AiOperationKind.Chat],
                [AiProtocolMode.Auto]));

        var resolver = Substitute.For<IAiRuntimeResolver>();
        resolver.ResolveChatRuntimeAsync(Arg.Any<Guid>(), AiPurpose.ReviewVerification, Arg.Any<CancellationToken>())
            .Returns(runtime);

        return new AiFindingMergeJudge(resolver);
    }

    private static CandidateReviewFinding Finding(string message, string filePath = "src/A.cs", int? lineNumber = 10)
    {
        return new CandidateReviewFinding(
            Guid.NewGuid().ToString("N"),
            new CandidateFindingProvenance(
                CandidateFindingProvenance.PerFileCommentOrigin,
                "per_file_review",
                filePath,
                reviewPassKind: ReviewPassKind.Baseline),
            CommentSeverity.Warning,
            message,
            CandidateReviewFinding.PerFileCommentCategory,
            filePath,
            lineNumber);
    }
}

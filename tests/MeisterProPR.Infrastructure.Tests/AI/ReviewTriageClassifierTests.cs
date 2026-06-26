// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Services;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>
///     The model-backed complexity classifier parses the model's verdict and falls back to the
///     deterministic size heuristic when the ReviewTriage binding is missing, the call fails, or the
///     response is unparseable. Never throws.
/// </summary>
public sealed class ReviewTriageClassifierTests
{
    private static ChangedFile SmallFile()
    {
        return new ChangedFile("src/A.cs", ChangeType.Edit, "var a = 1;", "@@ -1,1 +1,1 @@\n+var a = 1;\n");
    }

    [Fact]
    public async Task ClassifyAsync_NoBinding_FallsBackToSizeHeuristic()
    {
        var file = SmallFile();
        var sut = CreateClassifier(null);

        var verdict = await sut.ClassifyAsync(Guid.NewGuid(), file, FanOutSignal.Unavailable, [], CancellationToken.None);

        Assert.Equal(ReviewDiffProcessor.ClassifyTier(file), verdict.Tier);
        Assert.False(verdict.SecurityEscalate);
        Assert.Contains("fallback", verdict.Why, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClassifyAsync_ParsesModelVerdict()
    {
        var sut = CreateClassifier(ChatReturning("{\"tier\":\"high\",\"securityEscalate\":true,\"why\":\"touches auth\"}"));

        var verdict = await sut.ClassifyAsync(Guid.NewGuid(), SmallFile(), FanOutSignal.Measured(3), ["src/A.cs", "src/B.cs"], CancellationToken.None);

        Assert.Equal(FileComplexityTier.High, verdict.Tier);
        Assert.True(verdict.SecurityEscalate);
        Assert.Equal("touches auth", verdict.Why);
    }

    [Fact]
    public async Task ClassifyAsync_ModelWrapsJsonInCodeFence_StillParses()
    {
        var sut = CreateClassifier(ChatReturning("```json\n{\"tier\":\"medium\",\"securityEscalate\":false,\"why\":\"ok\"}\n```"));

        var verdict = await sut.ClassifyAsync(Guid.NewGuid(), SmallFile(), FanOutSignal.Unavailable, [], CancellationToken.None);

        Assert.Equal(FileComplexityTier.Medium, verdict.Tier);
        Assert.False(verdict.SecurityEscalate);
    }

    [Fact]
    public async Task ClassifyAsync_UnparseableResponse_FallsBackToSizeHeuristic()
    {
        var file = SmallFile();
        var sut = CreateClassifier(ChatReturning("sorry, I cannot help with that"));

        var verdict = await sut.ClassifyAsync(Guid.NewGuid(), file, FanOutSignal.Unavailable, [], CancellationToken.None);

        Assert.Equal(ReviewDiffProcessor.ClassifyTier(file), verdict.Tier);
        Assert.False(verdict.SecurityEscalate);
    }

    private static ReviewTriageClassifier CreateClassifier(IChatClient? chatClient)
    {
        var resolver = Substitute.For<IAiRuntimeResolver>();
        if (chatClient is null)
        {
            resolver.ResolveChatRuntimeAsync(Arg.Any<Guid>(), AiPurpose.ReviewTriage, Arg.Any<CancellationToken>())
                .ThrowsAsync(new InvalidOperationException("no active ReviewTriage binding"));
        }
        else
        {
            var runtime = Substitute.For<IResolvedAiChatRuntime>();
            runtime.ChatClient.Returns(chatClient);
            resolver.ResolveChatRuntimeAsync(Arg.Any<Guid>(), AiPurpose.ReviewTriage, Arg.Any<CancellationToken>())
                .Returns(runtime);
        }

        return new ReviewTriageClassifier(resolver, NullLogger<ReviewTriageClassifier>.Instance);
    }

    private static IChatClient ChatReturning(string text)
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        return client;
    }
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Features.CodeInsights.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.CodeInsights.Classification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.CodeInsights;

public sealed class AiHumanMissClassifierTests
{
    private static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task JudgeAsync_ReturnsTheThreeJudgementsSeparately()
    {
        var sut = CreateClassifier("""{"isSubstantive":true,"wasActedOn":true,"isInScope":false,"confidence":0.75,"rationale":"Needs a product decision."}""");

        var judgement = await sut.JudgeAsync(CreateRequest());

        Assert.NotNull(judgement);
        Assert.True(judgement.IsSubstantive);
        Assert.True(judgement.WasActedOn);
        // Kept separately so a change to where the scope cut-off sits can be re-applied without re-judging.
        Assert.False(judgement.IsInScope);
        Assert.Equal(0.75, judgement.Confidence, 3);
        Assert.Contains("product decision", judgement.Rationale, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"wasActedOn":true,"isInScope":true}""")]
    [InlineData("""{"isSubstantive":true,"isInScope":true}""")]
    [InlineData("""{"isSubstantive":true,"wasActedOn":true}""")]
    [InlineData("""{"isSubstantive":"yes","wasActedOn":true,"isInScope":true}""")]
    public async Task JudgeAsync_AnIncompleteJudgementIsDiscardedRatherThanDefaulted(string body)
    {
        // Defaulting a missing judgement would decide a recall number on something the model never said, and
        // every default is wrong in one direction or the other.
        var sut = CreateClassifier(body);

        Assert.Null(await sut.JudgeAsync(CreateRequest()));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("""[true,true,true]""")]
    public async Task JudgeAsync_AnUnusableResponseReportsNothing(string body)
    {
        var sut = CreateClassifier(body);

        Assert.Null(await sut.JudgeAsync(CreateRequest()));
    }

    [Fact]
    public async Task JudgeAsync_ClampsConfidenceIntoRange()
    {
        var sut = CreateClassifier("""{"isSubstantive":true,"wasActedOn":true,"isInScope":true,"confidence":42}""");

        var judgement = await sut.JudgeAsync(CreateRequest());

        Assert.Equal(1d, judgement!.Confidence);
    }

    [Fact]
    public async Task JudgeAsync_WithNoModelBound_ReportsNothingAndDoesNotThrow()
    {
        var resolver = Substitute.For<IAiRuntimeResolver>();
        resolver.ResolveChatRuntimeAsync(ClientId, AiPurpose.InsightsClassification, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AiPurposeBindingNotConfiguredException(AiPurpose.InsightsClassification));

        var sut = new AiHumanMissClassifier(resolver, NullLogger<AiHumanMissClassifier>.Instance);

        Assert.Null(await sut.JudgeAsync(CreateRequest()));
    }

    [Fact]
    public async Task JudgeAsync_WhenTheCallFails_ReportsNothingAndDoesNotThrow()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("the provider is down"));

        var sut = new AiHumanMissClassifier(
            CreateResolver(chatClient),
            NullLogger<AiHumanMissClassifier>.Instance);

        Assert.Null(await sut.JudgeAsync(CreateRequest()));
    }

    [Fact]
    public async Task JudgeAsync_AsksTheThreeQuestionsIndependentlyAndSaysWhatOutOfScopeMeans()
    {
        var captured = new List<ChatMessage>();
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(messages => captured.AddRange(messages)),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        """{"isSubstantive":true,"wasActedOn":true,"isInScope":true,"confidence":0.5}""")));

        var sut = new AiHumanMissClassifier(
            CreateResolver(chatClient),
            NullLogger<AiHumanMissClassifier>.Instance);

        await sut.JudgeAsync(CreateRequest());

        var system = captured.First(message => message.Role == ChatRole.System).Text;

        // A thread can easily be substantive and out of scope; conflating the two would either inflate recall
        // with issues no reviewer could catch, or hide real ones.
        Assert.Contains("on its own merits", system, StringComparison.Ordinal);
        Assert.Contains("substantive and out of scope", system, StringComparison.Ordinal);
        // "Out of scope" has to be defined or the model will read it as "hard".
        Assert.Contains("knowledge the code does not contain", system, StringComparison.Ordinal);
        // A resolved marker is evidence, not proof.
        Assert.Contains("housekeeping", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JudgeAsync_BoundsTheDiscussionItSends()
    {
        var captured = new List<ChatMessage>();
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(messages => captured.AddRange(messages)),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        """{"isSubstantive":true,"wasActedOn":true,"isInScope":true,"confidence":0.5}""")));

        var sut = new AiHumanMissClassifier(
            CreateResolver(chatClient),
            NullLogger<AiHumanMissClassifier>.Instance);

        await sut.JudgeAsync(CreateRequest() with { Discussion = new string('z', 40_000) });

        var user = captured.First(message => message.Role == ChatRole.User).Text;
        Assert.Contains("(truncated)", user, StringComparison.Ordinal);
        Assert.True(user.Length < 8_000, $"The prompt should be bounded, was {user.Length} chars.");
    }

    private static AiHumanMissClassifier CreateClassifier(string responseText)
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));

        return new AiHumanMissClassifier(
            CreateResolver(chatClient),
            NullLogger<AiHumanMissClassifier>.Instance);
    }

    private static IAiRuntimeResolver CreateResolver(IChatClient chatClient)
    {
        var runtime = Substitute.For<IResolvedAiChatRuntime>();
        runtime.ChatClient.Returns(chatClient);

        var resolver = Substitute.For<IAiRuntimeResolver>();
        resolver.ResolveChatRuntimeAsync(ClientId, AiPurpose.InsightsClassification, Arg.Any<CancellationToken>())
            .Returns(runtime);
        return resolver;
    }

    private static HumanMissJudgementRequest CreateRequest()
    {
        return new HumanMissJudgementRequest(
            ClientId,
            "thread-9",
            "src/Service.cs",
            "alice: this drops the retry count silently\nbob: good catch, fixed",
            true);
    }
}

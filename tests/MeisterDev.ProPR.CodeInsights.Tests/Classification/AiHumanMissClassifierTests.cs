// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.CodeInsights.Classification;

namespace MeisterDev.ProPR.CodeInsights.Tests.Classification;

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

        var sut = new AiHumanMissClassifier(resolver, Substitute.For<IModelUsageRecorder>(), NullLogger<AiHumanMissClassifier>.Instance);

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
            Substitute.For<IModelUsageRecorder>(),
            NullLogger<AiHumanMissClassifier>.Instance);

        Assert.Null(await sut.JudgeAsync(CreateRequest()));
    }

    [Theory]
    [InlineData("binding")]
    [InlineData("runtime")]
    [InlineData("client")]
    [InlineData("prompt")]
    public async Task JudgeWithAttemptAsync_SetupFailuresDoNotReportAModelRequest(string failure)
    {
        var chat = Substitute.For<IChatClient>();
        var runtime = Substitute.For<IResolvedAiChatRuntime>();
        runtime.ChatClient.Returns(failure == "client" ? null! : chat);
        var resolver = Substitute.For<IAiRuntimeResolver>();
        if (failure is "binding" or "runtime")
        {
            resolver.ResolveChatRuntimeAsync(ClientId, AiPurpose.InsightsClassification, Arg.Any<CancellationToken>())
                .ThrowsAsync(
                    failure == "binding"
                        ? new AiPurposeBindingNotConfiguredException(AiPurpose.InsightsClassification)
                        : new InvalidOperationException("Runtime unavailable"));
        }
        else
        {
            resolver.ResolveChatRuntimeAsync(ClientId, AiPurpose.InsightsClassification, Arg.Any<CancellationToken>()).Returns(runtime);
        }

        var sut = new AiHumanMissClassifier(resolver, Substitute.For<IModelUsageRecorder>(), NullLogger<AiHumanMissClassifier>.Instance);
        var request = failure == "prompt" ? CreateRequest() with { Discussion = null! } : CreateRequest();

        var result = await sut.JudgeWithAttemptAsync(request);

        Assert.Null(result.Judgement);
        Assert.False(result.ModelWasAsked);
        await chat.DidNotReceive().GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task JudgeWithAttemptAsync_FailedOrUnusableCallsReportAModelAttempt(bool providerFails)
    {
        var chat = Substitute.For<IChatClient>();
        if (providerFails)
        {
            chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new HttpRequestException("Provider unavailable"));
        }
        else
        {
            chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
                .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Unusable judgement")));
        }

        var sut = new AiHumanMissClassifier(CreateResolver(chat), Substitute.For<IModelUsageRecorder>(), NullLogger<AiHumanMissClassifier>.Instance);

        var result = await sut.JudgeWithAttemptAsync(CreateRequest());

        Assert.Null(result.Judgement);
        Assert.True(result.ModelWasAsked);
        await chat.Received(1).GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JudgeWithAttemptAsync_SuccessfulJudgementReportsAModelAttempt()
    {
        IHumanMissClassifier classifier = CreateClassifier("""{"isSubstantive":true,"wasActedOn":true,"isInScope":true,"confidence":0.9}""");
        var reporter = Assert.IsAssignableFrom<IHumanMissClassifierAttemptReporter>(classifier);

        var result = await reporter.JudgeWithAttemptAsync(CreateRequest());

        Assert.True(result.ModelWasAsked);
        Assert.NotNull(result.Judgement);
        Assert.True(result.Judgement.IsSubstantive);
        Assert.True(result.Judgement.WasActedOn);
        Assert.True(result.Judgement.IsInScope);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BothJudgementEntryPointsPropagateCancellation(bool duringResolution)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        var resolver = CreateResolver(chat);
        if (duringResolution)
        {
            resolver.ResolveChatRuntimeAsync(ClientId, AiPurpose.InsightsClassification, Arg.Any<CancellationToken>())
                .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        }

        var sut = new AiHumanMissClassifier(resolver, Substitute.For<IModelUsageRecorder>(), NullLogger<AiHumanMissClassifier>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.JudgeAsync(CreateRequest(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.JudgeWithAttemptAsync(CreateRequest(), cancellation.Token));
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
            Substitute.For<IModelUsageRecorder>(),
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
            Substitute.For<IModelUsageRecorder>(),
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
            Substitute.For<IModelUsageRecorder>(),
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

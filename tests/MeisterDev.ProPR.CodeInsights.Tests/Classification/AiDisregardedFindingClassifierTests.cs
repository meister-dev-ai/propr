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

public sealed class AiDisregardedFindingClassifierTests
{
    private static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FindingId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task JudgeAsync_ReadsAWrongVerdictWithItsConfidenceAndRationale()
    {
        var sut = CreateClassifier("""{"wasWrong":true,"confidence":0.88,"rationale":"The reviewer misread the guard clause."}""");

        var judgement = await sut.JudgeAsync(CreateRequest());

        Assert.NotNull(judgement);
        Assert.True(judgement.WasWrong);
        Assert.Equal(0.88, judgement.Confidence, 3);
        Assert.Contains("misread", judgement.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JudgeAsync_ReadsACorrectButUnwantedVerdict()
    {
        var sut = CreateClassifier("""{"wasWrong":false,"confidence":0.6,"rationale":"Tracked elsewhere."}""");

        var judgement = await sut.JudgeAsync(CreateRequest());

        Assert.False(judgement!.WasWrong);
    }

    [Theory]
    [InlineData("wrong", CodeInsightRejectionReason.Wrong, true)]
    [InlineData("out_of_scope", CodeInsightRejectionReason.OutOfScope, false)]
    [InlineData("redundant", CodeInsightRejectionReason.Redundant, false)]
    [InlineData("design_trade_off", CodeInsightRejectionReason.DesignTradeOff, false)]
    [InlineData("developer_preference", CodeInsightRejectionReason.DeveloperPreference, false)]
    public async Task JudgeAsync_ReadsTheReasonAlongsideTheVerdict(
        string token,
        CodeInsightRejectionReason expected,
        bool wasWrong)
    {
        // One call answers both questions. They are read off the same discussion, so asking twice would double
        // the cost of every rejection to learn nothing new.
        var sut = CreateClassifier($$"""{"wasWrong":{{(wasWrong ? "true" : "false")}},"reason":"{{token}}","confidence":0.7,"rationale":"x"}""");

        var judgement = await sut.JudgeAsync(CreateRequest());

        Assert.Equal(expected, judgement!.Reason);
        Assert.Equal(wasWrong, judgement.WasWrong);
    }

    [Theory]
    [InlineData("out-of-scope")]
    [InlineData("Out Of Scope")]
    [InlineData("  OUT_OF_SCOPE ")]
    public async Task JudgeAsync_ToleratesTheSeparatorsAModelReachesFor(string token)
    {
        var sut = CreateClassifier($$"""{"wasWrong":false,"reason":"{{token}}","confidence":0.5}""");

        var judgement = await sut.JudgeAsync(CreateRequest());

        Assert.Equal(CodeInsightRejectionReason.OutOfScope, judgement!.Reason);
    }

    [Theory]
    [InlineData("""{"wasWrong":false,"confidence":0.5}""")]
    [InlineData("""{"wasWrong":false,"reason":null,"confidence":0.5}""")]
    [InlineData("""{"wasWrong":false,"reason":"something we never defined","confidence":0.5}""")]
    public async Task JudgeAsync_WithoutAUsableReason_KeepsTheVerdictAndReportsNoReason(string body)
    {
        // An unjudged reason costs the reason only. Discarding a usable outcome with it would lose an outcome
        // that was established, and a guessed reason would put a number nobody established into a distribution.
        var sut = CreateClassifier(body);

        var judgement = await sut.JudgeAsync(CreateRequest());

        Assert.NotNull(judgement);
        Assert.False(judgement.WasWrong);
        Assert.Null(judgement.Reason);
    }

    [Theory]
    [InlineData(true, "out_of_scope")]
    [InlineData(false, "wrong")]
    public async Task JudgeAsync_WithAReasonThatContradictsTheVerdict_KeepsTheVerdict(bool wasWrong, string token)
    {
        // The verdict is the narrower question and the prompt asks for it first. Letting the reason win would
        // make a rejection reason able to move precision.
        var sut = CreateClassifier($$"""{"wasWrong":{{(wasWrong ? "true" : "false")}},"reason":"{{token}}","confidence":0.7}""");

        var judgement = await sut.JudgeAsync(CreateRequest());

        Assert.Equal(wasWrong, judgement!.WasWrong);
        Assert.Null(judgement.Reason);
    }

    [Fact]
    public async Task JudgeAsync_ReadsAnUnresolvedThreadAsNeitherAcceptedNorRejected()
    {
        // A human engaged and nobody decided. Reporting that as a rejection would charge the reviewer for a
        // verdict nobody gave.
        var sut = CreateClassifier("""{"wasWrong":false,"unresolved":true,"confidence":0.6,"rationale":"They argued and moved on."}""");

        var judgement = await sut.JudgeAsync(CreateRequest());

        Assert.True(judgement!.IsUnresolved);
        Assert.False(judgement.WasWrong);
        // Nothing was rejected, so there is no rejection to explain.
        Assert.Null(judgement.Reason);
    }

    [Fact]
    public async Task JudgeAsync_WithUnresolvedSet_IgnoresAReasonThatCameWithIt()
    {
        var sut = CreateClassifier("""{"wasWrong":true,"unresolved":true,"reason":"wrong","confidence":0.5}""");

        var judgement = await sut.JudgeAsync(CreateRequest());

        Assert.True(judgement!.IsUnresolved);
        Assert.Null(judgement.Reason);
        Assert.False(judgement.WasWrong);
    }

    [Fact]
    public async Task JudgeAsync_WithoutUnresolved_StaysAVerdict()
    {
        var sut = CreateClassifier("""{"wasWrong":false,"unresolved":false,"reason":"redundant","confidence":0.9}""");

        var judgement = await sut.JudgeAsync(CreateRequest());

        Assert.False(judgement!.IsUnresolved);
        Assert.Equal(CodeInsightRejectionReason.Redundant, judgement.Reason);
    }

    [Fact]
    public void ClassifierVersion_ChangesWhenTheQuestionChanges()
    {
        // The version is stamped onto every disposition. A judgement made under the two-value question must stay
        // distinguishable from one made under the five-reason question, or a later calibration cannot tell them
        // apart.
        var sut = CreateClassifier("{}");

        Assert.Equal("disregarded-split-v3", sut.ClassifierVersion);
    }

    [Theory]
    [InlineData("""{"confidence":0.9,"rationale":"no verdict at all"}""")]
    [InlineData("""{"wasWrong":"maybe","confidence":0.9}""")]
    [InlineData("not json")]
    [InlineData("")]
    public async Task JudgeAsync_WithoutAnExplicitVerdict_ReportsNothing(string body)
    {
        // Defaulting either way would put a fabricated judgement into a number that is meant to be evidence.
        var sut = CreateClassifier(body);

        Assert.Null(await sut.JudgeAsync(CreateRequest()));
    }

    [Theory]
    [InlineData("""{"wasWrong":true,"confidence":9}""", 1d)]
    [InlineData("""{"wasWrong":true,"confidence":-1}""", 0d)]
    [InlineData("""{"wasWrong":true}""", 0d)]
    public async Task JudgeAsync_ClampsConfidenceIntoRange(string body, double expected)
    {
        var sut = CreateClassifier(body);

        var judgement = await sut.JudgeAsync(CreateRequest());

        Assert.Equal(expected, judgement!.Confidence);
    }

    [Fact]
    public async Task JudgeAsync_WithNoModelBound_ReportsNothingAndDoesNotThrow()
    {
        var resolver = Substitute.For<IAiRuntimeResolver>();
        resolver.ResolveChatRuntimeAsync(ClientId, AiPurpose.InsightsClassification, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AiPurposeBindingNotConfiguredException(AiPurpose.InsightsClassification));

        var sut = new AiDisregardedFindingClassifier(
            resolver,
            Substitute.For<IModelUsageRecorder>(),
            NullLogger<AiDisregardedFindingClassifier>.Instance);

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

        var sut = new AiDisregardedFindingClassifier(
            CreateResolver(chatClient),
            Substitute.For<IModelUsageRecorder>(),
            NullLogger<AiDisregardedFindingClassifier>.Instance);

        Assert.Null(await sut.JudgeAsync(CreateRequest()));
    }

    [Fact]
    public async Task JudgeAsync_TellsTheModelNotToInferWrongnessFromInactionAndSendsTheDiscussion()
    {
        var captured = new List<ChatMessage>();
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(messages => captured.AddRange(messages)),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"wasWrong":false,"confidence":0.5}""")));

        var sut = new AiDisregardedFindingClassifier(
            CreateResolver(chatClient),
            Substitute.For<IModelUsageRecorder>(),
            NullLogger<AiDisregardedFindingClassifier>.Instance);

        await sut.JudgeAsync(CreateRequest());

        var system = captured.First(message => message.Role == ChatRole.System).Text;
        var user = captured.First(message => message.Role == ChatRole.User).Text;

        // The whole reason this classifier exists is that silence and rejection look identical from outside,
        // so the prompt has to say so explicitly or the model will read inaction as wrongness.
        Assert.Contains("Do NOT infer", system, StringComparison.Ordinal);
        Assert.Contains("dev: not now, tracked elsewhere", user, StringComparison.Ordinal);
        Assert.Contains("The null check is missing.", user, StringComparison.Ordinal);
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
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"wasWrong":false,"confidence":0.5}""")));

        var sut = new AiDisregardedFindingClassifier(
            CreateResolver(chatClient),
            Substitute.For<IModelUsageRecorder>(),
            NullLogger<AiDisregardedFindingClassifier>.Instance);

        await sut.JudgeAsync(CreateRequest() with { CommentHistory = new string('y', 40_000) });

        var user = captured.First(message => message.Role == ChatRole.User).Text;
        Assert.Contains("(truncated)", user, StringComparison.Ordinal);
        Assert.True(user.Length < 12_000, $"The prompt should be bounded, was {user.Length} chars.");
    }

    private static AiDisregardedFindingClassifier CreateClassifier(string responseText)
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));

        return new AiDisregardedFindingClassifier(
            CreateResolver(chatClient),
            Substitute.For<IModelUsageRecorder>(),
            NullLogger<AiDisregardedFindingClassifier>.Instance);
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

    private static DisregardedFindingJudgementRequest CreateRequest()
    {
        return new DisregardedFindingJudgementRequest(
            ClientId,
            FindingId,
            "The null check is missing.",
            "src/Service.cs",
            "dev: not now, tracked elsewhere",
            "@@ -1 +1 @@");
    }
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

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

    // Files are classified concurrently, and resolution reads several repositories over one scoped DbContext.
    // Overlapping those reads threw "a second operation was started on this context instance", which this class
    // caught and reported as a fault, so every file silently fell back to the size heuristic. Resolving once is
    // what removes the overlap, so the count is the regression guard.
    [Fact]
    public async Task ClassifyAsync_ConcurrentFiles_ResolveTheRuntimeOnlyOnce()
    {
        var resolutions = 0;
        var resolver = Substitute.For<IAiRuntimeResolver>();
        var chatClient = ChatReturning("{\"tier\":\"low\",\"securityEscalate\":false,\"why\":\"ok\"}");
        var runtime = Substitute.For<IResolvedAiChatRuntime>();
        runtime.ChatClient.Returns(chatClient);
        resolver.ResolveChatRuntimeAsync(Arg.Any<Guid>(), AiPurpose.ReviewTriage, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref resolutions);
                return Task.FromResult(runtime);
            });

        using var sut = new ReviewTriageClassifier(resolver, NullLogger<ReviewTriageClassifier>.Instance);
        var clientId = Guid.NewGuid();

        await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ =>
                sut.ClassifyAsync(clientId, SmallFile(), FanOutSignal.Unavailable, [], CancellationToken.None)));

        Assert.Equal(1, resolutions);
    }

    // A resolution that failed must not be remembered as a failure, or one transient fault would disable
    // model-judged triage for every remaining file in the job.
    [Fact]
    public async Task ClassifyAsync_AFailedResolutionIsRetriedForTheNextFile()
    {
        var attempts = 0;
        var resolver = Substitute.For<IAiRuntimeResolver>();
        var runtime = Runtime(ChatReturning("{\"tier\":\"high\",\"securityEscalate\":false,\"why\":\"ok\"}"));
        resolver.ResolveChatRuntimeAsync(Arg.Any<Guid>(), AiPurpose.ReviewTriage, Arg.Any<CancellationToken>())
            .Returns(_ => Interlocked.Increment(ref attempts) == 1
                ? Task.FromException<IResolvedAiChatRuntime>(new InvalidOperationException("transient"))
                : Task.FromResult(runtime));

        using var sut = new ReviewTriageClassifier(resolver, NullLogger<ReviewTriageClassifier>.Instance);
        var clientId = Guid.NewGuid();

        var first = await sut.ClassifyAsync(clientId, SmallFile(), FanOutSignal.Unavailable, [], CancellationToken.None);
        var second = await sut.ClassifyAsync(clientId, SmallFile(), FanOutSignal.Unavailable, [], CancellationToken.None);

        Assert.Contains("fallback", first.Why, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(FileComplexityTier.High, second.Tier);
    }

    // Triage was the one model caller that reported nothing it spent, so its tokens were billed by the provider
    // and counted by no one. Invisible spend rather than free spend, and it grows with the file count.
    [Fact]
    public async Task ClassifyAsync_ReportsWhatTheTriageCallSpent()
    {
        var recorder = Substitute.For<IModelUsageRecorder>();
        var chatClient = ChatReturning("{\"tier\":\"low\",\"securityEscalate\":false,\"why\":\"ok\"}");
        var runtime = Substitute.For<IResolvedAiChatRuntime>();
        runtime.ChatClient.Returns(chatClient);
        var resolver = Substitute.For<IAiRuntimeResolver>();
        resolver.ResolveChatRuntimeAsync(Arg.Any<Guid>(), AiPurpose.ReviewTriage, Arg.Any<CancellationToken>())
            .Returns(runtime);

        using var sut = new ReviewTriageClassifier(resolver, NullLogger<ReviewTriageClassifier>.Instance, recorder);
        var clientId = Guid.NewGuid();

        await sut.ClassifyAsync(clientId, SmallFile(), FanOutSignal.Unavailable, [], CancellationToken.None);

        await recorder.Received(1).RecordAsync(clientId, runtime, Arg.Any<ChatResponse?>(), Arg.Any<CancellationToken>());
    }

    // The tokens are spent whether or not the answer parses, so an unusable verdict is still reported spend.
    [Fact]
    public async Task ClassifyAsync_ReportsSpendEvenWhenTheVerdictIsUnusable()
    {
        var recorder = Substitute.For<IModelUsageRecorder>();
        var chatClient = ChatReturning("sorry, I cannot help with that");
        var runtime = Substitute.For<IResolvedAiChatRuntime>();
        runtime.ChatClient.Returns(chatClient);
        var resolver = Substitute.For<IAiRuntimeResolver>();
        resolver.ResolveChatRuntimeAsync(Arg.Any<Guid>(), AiPurpose.ReviewTriage, Arg.Any<CancellationToken>())
            .Returns(runtime);

        using var sut = new ReviewTriageClassifier(resolver, NullLogger<ReviewTriageClassifier>.Instance, recorder);

        var verdict = await sut.ClassifyAsync(Guid.NewGuid(), SmallFile(), FanOutSignal.Unavailable, [], CancellationToken.None);

        Assert.Contains("fallback", verdict.Why, StringComparison.OrdinalIgnoreCase);
        await recorder.Received(1).RecordAsync(Arg.Any<Guid>(), runtime, Arg.Any<ChatResponse?>(), Arg.Any<CancellationToken>());
    }

    // Triage runs before the file has a protocol to bill against, so the verdict carries what the call cost and
    // the caller attributes it once one exists. Without that the job reports less than it spent, by an amount
    // that grows with the file count.
    [Fact]
    public async Task ClassifyAsync_CarriesWhatTheCallSpentOnTheVerdict()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"tier\":\"high\"}"))
                {
                    Usage = new UsageDetails { InputTokenCount = 900, OutputTokenCount = 40, CachedInputTokenCount = 300 },
                });

        var verdict = await CreateClassifier(chatClient).ClassifyAsync(Guid.NewGuid(), SmallFile(), FanOutSignal.Unavailable, [], CancellationToken.None);

        Assert.NotNull(verdict.Spend);
        Assert.Equal(900, verdict.Spend!.InputTokens);
        Assert.Equal(40, verdict.Spend.OutputTokens);
        Assert.Equal(300, verdict.Spend.CachedInputTokens);
        Assert.Equal(FileComplexityTier.High, verdict.Tier);
    }

    // A response with no usage payload would otherwise add a breakdown line claiming the call was free, which
    // says more than "the provider did not report".
    [Fact]
    public async Task ClassifyAsync_WithNoUsageReported_CarriesNoSpend()
    {
        var verdict = await CreateClassifier(ChatReturning("{\"tier\":\"low\"}")).ClassifyAsync(
            Guid.NewGuid(), SmallFile(), FanOutSignal.Unavailable, [], CancellationToken.None);

        Assert.Null(verdict.Spend);
    }

    // The prompts moved out of this class into templates. What the model is asked stays the same, so the
    // instruction and the per-file facts are pinned here rather than left to the template alone.
    [Fact]
    public async Task ClassifyAsync_SendsTheTriagePromptsFromTheTemplates()
    {
        List<ChatMessage>? sent = null;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(messages => sent = messages.ToList()),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"tier\":\"low\"}")));

        var sut = CreateClassifier(chatClient);
        await sut.ClassifyAsync(
            Guid.NewGuid(),
            SmallFile(),
            FanOutSignal.Measured(3),
            ["src/A.cs", "src/B.cs"],
            CancellationToken.None);

        Assert.NotNull(sent);
        var system = Assert.Single(sent!, m => m.Role == ChatRole.System).Text;
        Assert.Contains("code-review triage classifier", system, StringComparison.Ordinal);
        Assert.Contains("securityEscalate", system, StringComparison.Ordinal);

        var user = Assert.Single(sent!, m => m.Role == ChatRole.User).Text;
        Assert.Contains("File: src/A.cs", user, StringComparison.Ordinal);
        Assert.Contains("Blast radius: 3 confirmed reference(s)", user, StringComparison.Ordinal);
        Assert.Contains("other changed files in this PR: src/B.cs", user, StringComparison.Ordinal);
        Assert.Contains("+var a = 1;", user, StringComparison.Ordinal);
    }

    /// <summary>A resolved runtime carrying the model and connection the spend is attributed to.</summary>
    private static IResolvedAiChatRuntime Runtime(IChatClient chatClient, string? logicalModelName = "Triage")
    {
        var model = new AiConfiguredModelDto(
            Guid.NewGuid(),
            "triage-model",
            "Triage Model",
            [AiOperationKind.Chat],
            [AiProtocolMode.ChatCompletions]);

        var connection = new AiConnectionDto(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Test",
            AiProviderKind.OpenAi,
            "https://api.openai.com/v1",
            AiAuthMode.ApiKey,
            AiDiscoveryMode.ManualOnly,
            true,
            [model],
            [],
            AiVerificationResultDto.NeverVerified,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        var runtime = Substitute.For<IResolvedAiChatRuntime>();
        runtime.ChatClient.Returns(chatClient);
        runtime.Model.Returns(model);
        runtime.Connection.Returns(connection);
        runtime.LogicalModelName.Returns(logicalModelName);
        return runtime;
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
            var runtime = Runtime(chatClient);
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

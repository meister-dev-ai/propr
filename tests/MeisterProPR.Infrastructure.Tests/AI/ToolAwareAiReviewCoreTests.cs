using System.Text.Json;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>
///     Unit tests for <see cref="ToolAwareAiReviewCore" />.
/// </summary>
public class ToolAwareAiReviewCoreTests
{
    private static IOptions<AiReviewOptions> DefaultOptions(
        int maxIterations = 5,
        int confidenceThreshold = 70)
    {
        return Microsoft.Extensions.Options.Options.Create(
            new AiReviewOptions
            {
                MaxIterations = maxIterations,
                ConfidenceThreshold = confidenceThreshold,
            });
    }

    private static PullRequest CreatePullRequest()
    {
        return new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            1,
            1,
            "Test PR",
            null,
            "feature/x",
            "main",
            new List<ChangedFile>().AsReadOnly());
    }

    private static ReviewSystemContext CreateContext(IReviewContextTools? tools = null)
    {
        return new ReviewSystemContext(null, [], tools);
    }

    private static ChatResponse CreateFinalReviewResponse(string summary = "All good.")
    {
        var json = JsonSerializer.Serialize(
            new
            {
                summary,
                comments = Array.Empty<object>(),
                confidence_evaluations = new[]
                {
                    new { concern = "code_quality", confidence = 90 },
                    new { concern = "security", confidence = 85 },
                },
                loop_complete = true,
            });
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, json));
    }

    private static ChatResponse CreateFunctionCallResponse(string callId, string functionName, string argsJson)
    {
        var funcCallContent = new FunctionCallContent(callId, functionName, JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson));
        var message = new ChatMessage(ChatRole.Assistant, [funcCallContent]);
        return new ChatResponse(message);
    }

    [Fact]
    public async Task ReviewAsync_LoopCompleteFlag_ExitsAfterFirstIteration()
    {
        // Arrange
        var mockClient = Substitute.For<IChatClient>();
        mockClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(CreateFinalReviewResponse("Looks good."));

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(maxIterations: 10),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        // Act
        var result = await sut.ReviewAsync(CreatePullRequest(), CreateContext());

        // Assert — should only call AI once, loop exits because loop_complete = true
        await mockClient.Received(1)
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());
        Assert.Equal("Looks good.", result.Summary);
    }

    [Fact]
    public async Task ReviewAsync_AllConfidenceAboveThreshold_ExitsLoop()
    {
        // Arrange
        var mockClient = Substitute.For<IChatClient>();
        var json = JsonSerializer.Serialize(
            new
            {
                summary = "High confidence review.",
                comments = Array.Empty<object>(),
                confidence_evaluations = new[]
                {
                    new { concern = "security", confidence = 95 },
                    new { concern = "correctness", confidence = 80 },
                },
                loop_complete = false, // not setting loop_complete, but threshold met
            });
        mockClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(10),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        // Act
        var result = await sut.ReviewAsync(CreatePullRequest(), CreateContext());

        // Assert — exits after one iteration because all scores >= 70
        await mockClient.Received(1)
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());
        Assert.Equal("High confidence review.", result.Summary);
    }

    [Fact]
    public async Task ReviewAsync_MaxIterationsReached_StopsLoop()
    {
        // Arrange — AI always returns a response that does NOT complete the loop and requires more work
        var mockClient = Substitute.For<IChatClient>();
        var lowConfidenceJson = JsonSerializer.Serialize(
            new
            {
                summary = "Still investigating.",
                comments = Array.Empty<object>(),
                confidence_evaluations = new[]
                {
                    new { concern = "security", confidence = 30 }, // below threshold
                },
                loop_complete = false,
            });
        mockClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, lowConfidenceJson)));

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(3),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        // Act
        var result = await sut.ReviewAsync(CreatePullRequest(), CreateContext());

        // Assert — loop runs exactly maxIterations (3) times then stops
        await mockClient.Received(3)
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_WithTools_DispatchesToolCallsToReviewContextTools()
    {
        // Arrange
        var mockClient = Substitute.For<IChatClient>();
        var mockTools = Substitute.For<IReviewContextTools>();
        mockTools.GetChangedFilesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChangedFileSummary>>([]));

        var callCount = 0;

        // First response: function call to get_changed_files
        // Second response: final review
        mockClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return Task.FromResult(
                        CreateFunctionCallResponse(
                            "call-1",
                            "get_changed_files",
                            "{}"));
                }

                return Task.FromResult(CreateFinalReviewResponse("Done."));
            });

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(maxIterations: 5),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        // Act
        var result = await sut.ReviewAsync(CreatePullRequest(), CreateContext(mockTools));

        // Assert — AI was called twice (once for tool call, once for final response)
        Assert.Equal(2, callCount);
        Assert.Equal("Done.", result.Summary);
    }

    [Fact]
    public async Task ReviewAsync_LoopMetricsPopulatedOnContext_AfterReview()
    {
        // Arrange
        var mockClient = Substitute.For<IChatClient>();
        var json = JsonSerializer.Serialize(
            new
            {
                summary = "Review complete.",
                comments = Array.Empty<object>(),
                confidence_evaluations = new[]
                {
                    new { concern = "quality", confidence = 80 },
                },
                loop_complete = true,
            });
        mockClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        var context = CreateContext();

        // Act
        await sut.ReviewAsync(CreatePullRequest(), context);

        // Assert — LoopMetrics is populated after ReviewAsync returns
        Assert.NotNull(context.LoopMetrics);
        Assert.Equal(0, context.LoopMetrics!.ToolCallCount); // no tool calls
        Assert.NotNull(context.LoopMetrics.ConfidenceEvaluationsJson); // confidence was recorded
    }

    [Fact]
    public async Task ReviewAsync_PureJsonWithoutConfidenceEvaluations_BreaksImmediately()
    {
        // Arrange — response without confidence_evaluations field treated as final
        var mockClient = Substitute.For<IChatClient>();
        var json = """{"summary":"Simple review.","comments":[]}""";
        mockClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(maxIterations: 10),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        // Act
        var result = await sut.ReviewAsync(CreatePullRequest(), CreateContext());

        // Assert — exits immediately, only one AI call
        await mockClient.Received(1)
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());
        Assert.Equal("Simple review.", result.Summary);
    }

    [Fact]
    public async Task ReviewAsync_WithProtocolRecorder_RecordsAiCallEvent()
    {
        // Arrange
        var mockClient = Substitute.For<IChatClient>();
        var json = """{"summary":"All good.","comments":[]}""";
        mockClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        var protocolId = Guid.NewGuid();
        var recorder = Substitute.For<IProtocolRecorder>();
        var context = new ReviewSystemContext(null, [], null)
        {
            ActiveProtocolId = protocolId,
            ProtocolRecorder = recorder,
        };

        // Act
        await sut.ReviewAsync(CreatePullRequest(), context);

        // Assert — AI call event was recorded
        await recorder.Received(1)
            .RecordAiCallAsync(
                protocolId,
                Arg.Any<int>(),
                Arg.Any<long?>(),
                Arg.Any<long?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_WithToolCallAndProtocolRecorder_RecordsToolCallEvent()
    {
        // Arrange
        var mockClient = Substitute.For<IChatClient>();
        var mockTools = Substitute.For<IReviewContextTools>();
        mockTools.GetChangedFilesAsync(Arg.Any<CancellationToken>()).Returns([]);

        var finalJson = """{"summary":"Done.","comments":[]}""";

        // First response: tool call; second response: final review
        var toolCallResponse = new ChatResponse(
            new ChatMessage(
                ChatRole.Assistant,
                [
                    new FunctionCallContent("call-1", "get_changed_files"),
                ]));
        var finalResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, finalJson));

        mockClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(toolCallResponse, finalResponse);

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        var protocolId = Guid.NewGuid();
        var recorder = Substitute.For<IProtocolRecorder>();
        var context = new ReviewSystemContext(null, [], mockTools)
        {
            ActiveProtocolId = protocolId,
            ProtocolRecorder = recorder,
        };

        // Act
        await sut.ReviewAsync(CreatePullRequest(), context);

        // Assert — tool call event was recorded
        await recorder.Received(1)
            .RecordToolCallAsync(
                protocolId,
                "get_changed_files",
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_WithProtocolRecorder_PopulatesTokensInLoopMetrics()
    {
        // Arrange
        var mockClient = Substitute.For<IChatClient>();
        var json = """{"summary":"All good.","comments":[]}""";
        var usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 50 };
        var chatResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, json)) { Usage = usage };
        mockClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(chatResponse);

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        var context = CreateContext();

        // Act
        await sut.ReviewAsync(CreatePullRequest(), context);

        // Assert — token counts accumulated in LoopMetrics
        Assert.NotNull(context.LoopMetrics);
        Assert.Equal(100L, context.LoopMetrics!.TotalInputTokens);
        Assert.Equal(50L, context.LoopMetrics.TotalOutputTokens);
    }
}

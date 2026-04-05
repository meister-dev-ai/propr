// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

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
        int confidenceThreshold = 70,
        string modelId = "")
    {
        return Microsoft.Extensions.Options.Options.Create(
            new AiReviewOptions
            {
                MaxIterations = maxIterations,
                ConfidenceThreshold = confidenceThreshold,
                ModelId = modelId,
            });
    }

    private static PullRequest CreatePullRequest()
    {
        return new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
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
    public async Task ReviewAsync_ContextModelId_OverridesGlobalFallbackModelId()
    {
        // Arrange
        string? observedModelId = null;
        var mockClient = Substitute.For<IChatClient>();
        mockClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                observedModelId = callInfo.Arg<ChatOptions?>()?.ModelId;
                return CreateFinalReviewResponse("Looks good.");
            });

        var context = CreateContext();
        context.ModelId = "client-configured-deployment";

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(modelId: "global-fallback-model"),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        // Act
        await sut.ReviewAsync(CreatePullRequest(), context);

        // Assert
        Assert.Equal("client-configured-deployment", observedModelId);
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
                1,
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

    // T017 — Per-file path: global System message absent from messages on iteration 2+
    [Fact]
    public async Task ReviewAsync_PerFilePath_GlobalSystemMessageAbsentOnIteration2Plus()
    {
        // Arrange — two tool calls to force three GetResponseAsync calls (iterations 1, 2, 3)
        var toolCallResponse1 = CreateFunctionCallResponse("c1", "get_changed_files", "{}");
        var toolCallResponse2 = CreateFunctionCallResponse("c2", "get_changed_files", "{}");
        var finalResponse = CreateFinalReviewResponse("Done.");

        var capturedCallArgs = new List<List<ChatMessage>>();
        var mockClient = Substitute.For<IChatClient>();
        mockClient
            .GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(msgs => capturedCallArgs.Add(msgs.ToList())),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(toolCallResponse1, toolCallResponse2, finalResponse);

        var mockTools = Substitute.For<IReviewContextTools>();
        mockTools.GetChangedFilesAsync(Arg.Any<CancellationToken>()).Returns([]);

        var file = new ChangedFile("src/Foo.cs", MeisterProPR.Domain.Enums.ChangeType.Edit, "code", "+code");
        var pr = new PullRequest("https://dev.azure.com/org", "proj", "repo", "repo", 1, 1, "PR", null, "feature/x", "main",
            new List<ChangedFile> { file }.AsReadOnly());

        var context = new ReviewSystemContext(null, [], mockTools)
        {
            PerFileHint = new PerFileReviewHint("src/Foo.cs", 1, 1, pr.AllPrFileSummaries),
        };

        var sut = new ToolAwareAiReviewCore(mockClient, DefaultOptions(maxIterations: 10), Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        // Act
        await sut.ReviewAsync(pr, context);

        // Assert — on iteration 1, two System messages (global + per-file)
        Assert.True(capturedCallArgs.Count >= 2, $"Expected at least 2 AI calls but got {capturedCallArgs.Count}");
        var iter1Messages = capturedCallArgs[0];
        var iter1SystemMsgs = iter1Messages.Where(m => m.Role == ChatRole.System).ToList();
        Assert.Equal(2, iter1SystemMsgs.Count);
        Assert.Contains(ReviewPrompts.SystemPrompt, iter1SystemMsgs[0].Text ?? "");

        // On iteration 2+, only one System message (per-file context; global dropped)
        var iter2Messages = capturedCallArgs[1];
        var iter2SystemMsgs = iter2Messages.Where(m => m.Role == ChatRole.System).ToList();
        Assert.Single(iter2SystemMsgs);
        Assert.DoesNotContain(ReviewPrompts.SystemPrompt, iter2SystemMsgs[0].Text ?? "");
        Assert.Contains("src/Foo.cs", iter2SystemMsgs[0].Text ?? "");
    }

    // T017 — RecordToolCallAsync receives iteration equal to current loop count
    [Fact]
    public async Task ReviewAsync_PerFilePath_RecordToolCallAsync_ReceivesCorrectIteration()
    {
        // Arrange — single tool call on iteration 1, final answer on iteration 2
        var toolCallResponse = CreateFunctionCallResponse("c1", "get_changed_files", "{}");
        var finalResponse = CreateFinalReviewResponse("Done.");

        var mockClient = Substitute.For<IChatClient>();
        mockClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(toolCallResponse, finalResponse);

        var mockTools = Substitute.For<IReviewContextTools>();
        mockTools.GetChangedFilesAsync(Arg.Any<CancellationToken>()).Returns([]);

        var file = new ChangedFile("src/Foo.cs", MeisterProPR.Domain.Enums.ChangeType.Edit, "code", "+code");
        var pr = new PullRequest("https://dev.azure.com/org", "proj", "repo", "repo", 1, 1, "PR", null, "feature/x", "main",
            new List<ChangedFile> { file }.AsReadOnly());

        var protocolId = Guid.NewGuid();
        var recorder = Substitute.For<IProtocolRecorder>();
        var context = new ReviewSystemContext(null, [], mockTools)
        {
            PerFileHint = new PerFileReviewHint("src/Foo.cs", 1, 1, pr.AllPrFileSummaries),
            ActiveProtocolId = protocolId,
            ProtocolRecorder = recorder,
        };

        var sut = new ToolAwareAiReviewCore(mockClient, DefaultOptions(maxIterations: 10), Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        // Act
        await sut.ReviewAsync(pr, context);

        // Assert — tool call was on iteration 1
        await recorder.Received(1).RecordToolCallAsync(
            protocolId,
            "get_changed_files",
            Arg.Any<string>(),
            Arg.Any<string>(),
            1,
            Arg.Any<CancellationToken>());
    }

    // T019 — Two per-file reviews of the same PR share a byte-for-byte identical first System message
    [Fact]
    public async Task ReviewAsync_TwoParallelPerFileReviews_GlobalSystemPromptIdentical()
    {
        // Arrange — two separate reviewAsync calls for different files of the same PR
        string? capturedSystem1 = null;
        string? capturedSystem2 = null;

        var mockClient1 = Substitute.For<IChatClient>();
        mockClient1
            .GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(msgs =>
                {
                    capturedSystem1 ??= msgs.FirstOrDefault(m => m.Role == ChatRole.System)?.Text;
                }),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateFinalReviewResponse("Done 1."));

        var mockClient2 = Substitute.For<IChatClient>();
        mockClient2
            .GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(msgs =>
                {
                    capturedSystem2 ??= msgs.FirstOrDefault(m => m.Role == ChatRole.System)?.Text;
                }),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateFinalReviewResponse("Done 2."));

        var files = new List<ChangedFile>
        {
            new("src/Foo.cs", MeisterProPR.Domain.Enums.ChangeType.Edit, "code1", "+code1"),
            new("src/Bar.cs", MeisterProPR.Domain.Enums.ChangeType.Edit, "code2", "+code2"),
        }.AsReadOnly();
        var pr = new PullRequest("https://dev.azure.com/org", "proj", "repo", "repo", 1, 1, "PR", null, "feature/x", "main", files);

        var sharedContext = new ReviewSystemContext("Custom system message for client", [], null);

        var context1 = new ReviewSystemContext(sharedContext.ClientSystemMessage, sharedContext.RepositoryInstructions, null)
        {
            PerFileHint = new PerFileReviewHint("src/Foo.cs", 1, 2, pr.AllPrFileSummaries),
        };
        var context2 = new ReviewSystemContext(sharedContext.ClientSystemMessage, sharedContext.RepositoryInstructions, null)
        {
            PerFileHint = new PerFileReviewHint("src/Bar.cs", 2, 2, pr.AllPrFileSummaries),
        };

        var sut1 = new ToolAwareAiReviewCore(mockClient1, DefaultOptions(), Substitute.For<ILogger<ToolAwareAiReviewCore>>());
        var sut2 = new ToolAwareAiReviewCore(mockClient2, DefaultOptions(), Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        // Act — simulate two parallel reviews of the same PR
        await Task.WhenAll(
            sut1.ReviewAsync(pr, context1),
            sut2.ReviewAsync(pr, context2));

        // Assert — the first System message (global persona) is byte-for-byte identical
        Assert.NotNull(capturedSystem1);
        Assert.NotNull(capturedSystem2);
        Assert.Equal(capturedSystem1, capturedSystem2);
    }

    // --- Malformed model output resilience ---

    [Fact]
    public async Task ReviewAsync_SummaryIsJsonArray_JoinedAsString()
    {
        // Arrange — model returns "summary" as an array of strings instead of a plain string
        var mockClient = Substitute.For<IChatClient>();
        var json = """
                   {
                     "summary": ["First finding.", "Second finding."],
                     "comments": [],
                     "confidence_evaluations": [{"concern":"correctness","confidence":80}],
                     "loop_complete": true
                   }
                   """;
        mockClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));

        var sut = new ToolAwareAiReviewCore(mockClient, DefaultOptions(), Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        // Act — must not throw
        var result = await sut.ReviewAsync(CreatePullRequest(), CreateContext());

        // Assert — array items joined by double newline
        Assert.Contains("First finding.", result.Summary);
        Assert.Contains("Second finding.", result.Summary);
    }

    [Fact]
    public async Task ReviewAsync_SummaryIsJsonObject_FallsBackToRawJson()
    {
        // Arrange — model returns "summary" as a nested object (e.g. {"high_level": "..."})
        var mockClient = Substitute.For<IChatClient>();
        var json = """
                   {
                     "summary": {"high_level": "Looks fine.", "details": "No issues found."},
                     "comments": [],
                     "confidence_evaluations": [{"concern":"correctness","confidence":90}],
                     "loop_complete": true
                   }
                   """;
        mockClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));

        var sut = new ToolAwareAiReviewCore(mockClient, DefaultOptions(), Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        // Act — must not throw
        var result = await sut.ReviewAsync(CreatePullRequest(), CreateContext());

        // Assert — raw JSON text preserved; specific object content is retained
        Assert.Contains("high_level", result.Summary);
        Assert.Contains("Looks fine.", result.Summary);
    }

    [Fact]
    public async Task ReviewAsync_ConfidenceIsStringEncodedNumber_ParsedCorrectly()
    {
        // Arrange — model returns "confidence": "85" (string) instead of 85 (number)
        var mockClient = Substitute.For<IChatClient>();
        var json = """
                   {
                     "summary": "Good.",
                     "comments": [],
                     "confidence_evaluations": [{"concern":"security","confidence":"85"}],
                     "loop_complete": true
                   }
                   """;
        mockClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));

        var sut = new ToolAwareAiReviewCore(mockClient, DefaultOptions(), Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        // Act — must not throw
        var result = await sut.ReviewAsync(CreatePullRequest(), CreateContext());

        Assert.Equal("Good.", result.Summary);
    }

    [Fact]
    public async Task ReviewAsync_ConfidenceIsStringWithPercent_ParsedCorrectly()
    {
        // Arrange — model returns "confidence": "90%" (decorated string)
        var mockClient = Substitute.For<IChatClient>();
        var json = """
                   {
                     "summary": "Good.",
                     "comments": [],
                     "confidence_evaluations": [{"concern":"correctness","confidence":"90%"}],
                     "loop_complete": true
                   }
                   """;
        mockClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));

        var sut = new ToolAwareAiReviewCore(mockClient, DefaultOptions(), Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        // Act — must not throw
        var result = await sut.ReviewAsync(CreatePullRequest(), CreateContext());

        Assert.Equal("Good.", result.Summary);
    }

    [Fact]
    public async Task ReviewAsync_ConfidenceEvaluationsContainsStringElement_SkipsItGracefully()
    {
        // Arrange — model returns a confidence_evaluations array where some elements are plain
        //           strings (e.g. "correctness: 85") rather than objects
        var mockClient = Substitute.For<IChatClient>();
        var json = """
                   {
                     "summary": "Fine.",
                     "comments": [],
                     "confidence_evaluations": [
                       "correctness: 85",
                       {"concern":"security","confidence":90}
                     ],
                     "loop_complete": true
                   }
                   """;
        mockClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));

        var sut = new ToolAwareAiReviewCore(mockClient, DefaultOptions(), Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        // Act — must not throw; string element skipped, valid object element processed
        var result = await sut.ReviewAsync(CreatePullRequest(), CreateContext());

        Assert.Equal("Fine.", result.Summary);
    }

    [Fact]
    public async Task ReviewAsync_ConfidenceEvaluationsAllStrings_TreatedAsEmptyList()
    {
        // Arrange — every element in confidence_evaluations is a string; no valid objects
        var mockClient = Substitute.For<IChatClient>();
        var json = """
                   {
                     "summary": "Fine.",
                     "comments": [],
                     "confidence_evaluations": ["correctness: 85", "security: 70"],
                     "loop_complete": true
                   }
                   """;
        mockClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));

        var sut = new ToolAwareAiReviewCore(mockClient, DefaultOptions(), Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        // Act — must not throw
        var result = await sut.ReviewAsync(CreatePullRequest(), CreateContext());

        Assert.Equal("Fine.", result.Summary);
        Assert.Empty(result.Comments);
    }

    [Fact]
    public async Task ReviewAsync_WrongSchema_NoCommentsKey_TriggersSchemaCorrectionCall()
    {
        // Arrange — first response uses a non-standard schema (no "comments" key at all).
        //           Second response (schema-correction call) uses the correct schema.
        var mockClient = Substitute.For<IChatClient>();
        var wrongSchemaJson = """
                              {
                                "file": "/src/Foo.cs",
                                "key_issues": [{"severity":"high","title":"Null ref","details":"..."}],
                                "investigation_complete": true,
                                "summary": "Several issues found."
                              }
                              """;
        var correctSchemaJson = """
                                {
                                  "summary": "Several issues found.",
                                  "comments": [
                                    {"file_path":"/src/Foo.cs","line_number":42,"severity":"error","message":"Null ref risk"}
                                  ],
                                  "confidence_evaluations": [{"concern":"correctness","confidence":80}],
                                  "investigation_complete": true,
                                  "loop_complete": true
                                }
                                """;

        var callCount = 0;
        mockClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return Task.FromResult(
                    new ChatResponse(new ChatMessage(ChatRole.Assistant,
                        callCount == 1 ? wrongSchemaJson : correctSchemaJson)));
            });

        var sut = new ToolAwareAiReviewCore(mockClient, DefaultOptions(), Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        // Act
        var result = await sut.ReviewAsync(CreatePullRequest(), CreateContext());

        // Assert — two AI calls: first returned wrong schema, second returned correct schema
        Assert.Equal(2, callCount);
        Assert.Single(result.Comments);
        Assert.Equal("/src/Foo.cs", result.Comments[0].FilePath);
        Assert.Equal("Null ref risk", result.Comments[0].Message);
    }

    [Fact]
    public async Task ReviewAsync_WrongSchema_CommentsKeyPresentButEmpty_NoSchemaCorrectionCall()
    {
        // Arrange — model returns the correct schema with an *empty* comments array (genuine no-issues).
        //           This must NOT trigger the schema-correction pass.
        var mockClient = Substitute.For<IChatClient>();
        var json = """
                   {
                     "summary": "No issues found.",
                     "comments": [],
                     "confidence_evaluations": [{"concern":"correctness","confidence":95}],
                     "investigation_complete": true,
                     "loop_complete": true
                   }
                   """;
        mockClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));

        var sut = new ToolAwareAiReviewCore(mockClient, DefaultOptions(), Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        // Act
        var result = await sut.ReviewAsync(CreatePullRequest(), CreateContext());

        // Assert — exactly one AI call; empty comments array is valid, no correction needed
        await mockClient.Received(1)
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        Assert.Equal("No issues found.", result.Summary);
        Assert.Empty(result.Comments);
    }

    [Fact]
    public async Task ReviewAsync_ResponseWrappedInMarkdownFence_StrippedBeforeDeserialization()
    {
        // Arrange — model wraps its JSON in ```json ... ``` fences
        var mockClient = Substitute.For<IChatClient>();
        var json = """
                   ```json
                   {"summary":"Fence stripped.","comments":[],"loop_complete":true}
                   ```
                   """;
        mockClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));

        var sut = new ToolAwareAiReviewCore(mockClient, DefaultOptions(), Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        // Act — must not throw
        var result = await sut.ReviewAsync(CreatePullRequest(), CreateContext());

        Assert.Equal("Fence stripped.", result.Summary);
    }

    // ─── T036: MaxIterationsOverride ────────────────────────────────────────────

    [Fact]
    public async Task ReviewAsync_MaxIterationsOverrideOnHint_OverridesGlobalMaxIterations()
    {
        // Arrange — AI always returns low confidence (loop never self-terminates)
        var mockClient = Substitute.For<IChatClient>();
        var lowConfidenceJson = JsonSerializer.Serialize(
            new
            {
                summary = "Still investigating.",
                comments = Array.Empty<object>(),
                confidence_evaluations = new[]
                {
                    new { concern = "security", confidence = 30 },
                },
                loop_complete = false,
            });
        mockClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, lowConfidenceJson)));

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(maxIterations: 10), // global max = 10
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        // PerFileHint.MaxIterationsOverride = 3 → loop must stop at 3, not 10
        var context = new ReviewSystemContext(null, [], null)
        {
            PerFileHint = new MeisterProPR.Application.ValueObjects.PerFileReviewHint(
                FilePath: "src/BigService.cs",
                FileIndex: 1,
                TotalFiles: 1,
                AllChangedFileSummaries: [])
            {
                MaxIterationsOverride = 3,
            },
        };

        // Act
        await sut.ReviewAsync(CreatePullRequest(), context);

        // Assert — override of 3 must win over global max of 10
        await mockClient.Received(3)
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());
    }
}

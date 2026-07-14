// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
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
    // The file-content tool prefixes every returned line with its absolute file line number so
    // the model reads anchors from the annotation instead of counting lines.
    [Fact]
    public void AnnotateFileContentToolResult_PrefixesLinesWithAbsoluteNumbers()
    {
        var annotated = ToolAwareAiReviewCore.AnnotateFileContentToolResult("alpha\nbravo", 41);

        Assert.Equal("41 | alpha\n42 | bravo", annotated);
    }

    [Fact]
    public void AnnotateFileContentToolResult_NonPositiveStart_ClampsToLineOne()
    {
        var annotated = ToolAwareAiReviewCore.AnnotateFileContentToolResult("alpha\nbravo", 0);

        Assert.Equal("1 | alpha\n2 | bravo", annotated);
    }

    [Fact]
    public void AnnotateFileContentToolResult_NoticePlaceholders_PassThroughUnchanged()
    {
        const string binaryNotice = "[Binary file — content not available: assets/logo.png]";
        const string tooLargeNotice = "[File too large: 2000000 bytes exceeds limit of 1000000 bytes]";

        Assert.Equal(binaryNotice, ToolAwareAiReviewCore.AnnotateFileContentToolResult(binaryNotice, 1));
        Assert.Equal(tooLargeNotice, ToolAwareAiReviewCore.AnnotateFileContentToolResult(tooLargeNotice, 1));
        Assert.Equal(string.Empty, ToolAwareAiReviewCore.AnnotateFileContentToolResult(string.Empty, 1));
        Assert.Equal(string.Empty, ToolAwareAiReviewCore.AnnotateFileContentToolResult(null, 1));
    }

    // Content that merely looks bracketed (e.g. a JSON array line) is still real file content
    // and must be annotated.
    [Fact]
    public void AnnotateFileContentToolResult_BracketedFileContent_IsStillAnnotated()
    {
        var annotated = ToolAwareAiReviewCore.AnnotateFileContentToolResult("[1, 2, 3]", 7);

        Assert.Equal("7 | [1, 2, 3]", annotated);
    }

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
        var funcCallContent = new FunctionCallContent(
            callId,
            functionName,
            JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson));
        var message = new ChatMessage(ChatRole.Assistant, [funcCallContent]);
        return new ChatResponse(message);
    }

    private static ChatResponse CreateMixedTextAndFunctionCallResponse(
        string text,
        string callId,
        string functionName,
        string argsJson)
    {
        var funcCallContent = new FunctionCallContent(
            callId,
            functionName,
            JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson));
        var message = new ChatMessage(ChatRole.Assistant, [new TextContent(text), funcCallContent]);
        return new ChatResponse(message);
    }

    private static ToolAwareAiReviewCore CreateSut(
        IChatClient? chatClient = null,
        IManagedReviewSessionTransportFactory? managedTransportFactory = null,
        int maxIterations = 5)
    {
        return new ToolAwareAiReviewCore(
            chatClient,
            DefaultOptions(maxIterations),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>(),
            managedTransportFactory);
    }

    [Fact]
    public async Task ReviewAsync_LoopCompleteFlag_ExitsAfterFirstIteration()
    {
        // Arrange
        var mockClient = Substitute.For<IChatClient>();
        mockClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateFinalReviewResponse("Looks good."));

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(10),
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
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
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
    public async Task ReviewAsync_ContextTemperature_UsesConfiguredTemperature()
    {
        float? observedTemperature = null;
        var mockClient = Substitute.For<IChatClient>();
        mockClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                observedTemperature = callInfo.Arg<ChatOptions?>()?.Temperature;
                return CreateFinalReviewResponse("Looks good.");
            });

        var context = CreateContext();
        context.Temperature = 0.22f;

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(modelId: "global-fallback-model"),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        await sut.ReviewAsync(CreatePullRequest(), context);

        Assert.Equal(0.22f, observedTemperature);
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
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
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
        var callCount = 0;
        mockClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                var lowConfidenceJson = JsonSerializer.Serialize(
                    new
                    {
                        summary = $"Still investigating {callCount}.",
                        comments = Array.Empty<object>(),
                        confidence_evaluations = new[]
                        {
                            new { concern = "security", confidence = 30 }, // below threshold
                        },
                        loop_complete = false,
                    });
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, lowConfidenceJson));
            });

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
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
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
            DefaultOptions(),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        // Act
        var result = await sut.ReviewAsync(CreatePullRequest(), CreateContext(mockTools));

        // Assert — AI was called twice (once for tool call, once for final response)
        Assert.Equal(2, callCount);
        Assert.Equal("Done.", result.Summary);
    }

    [Fact]
    public async Task ReviewAsync_WhenProCursorIsUnavailable_OmitsProCursorAiFunctions()
    {
        List<string> toolNames = [];
        var mockClient = Substitute.For<IChatClient>();
        mockClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var tools = callInfo.Arg<ChatOptions?>()?.Tools;
                toolNames = tools is null
                    ? []
                    : tools
                        .OfType<AIFunction>()
                        .Select(tool => tool.Name)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .ToList();

                return CreateFinalReviewResponse("Done.");
            });

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        await sut.ReviewAsync(CreatePullRequest(), CreateContext(new DisabledProCursorReviewTools()));

        Assert.Contains("get_changed_files", toolNames);
        Assert.Contains("get_file_tree", toolNames);
        Assert.Contains("get_file_content", toolNames);
        Assert.DoesNotContain("search_source_repo", toolNames);
        Assert.Contains("search_source_changed_files", toolNames);
        Assert.Contains("search_target_repo", toolNames);
        Assert.Contains("search_target_changed_files", toolNames);
        Assert.Contains("search_code", toolNames);
        Assert.Contains("search_paths", toolNames);
        Assert.Contains("get_repository_overview", toolNames);
        Assert.Contains("get_file_neighborhood", toolNames);
        Assert.DoesNotContain("ask_procursor_knowledge", toolNames);
        Assert.DoesNotContain("get_procursor_symbol_info", toolNames);
    }

    // The structural tools register for all languages and COEXIST with the ProCursor knowledge tools
    // (the code-analysis re-route must not unregister the knowledge surface).
    [Fact]
    public async Task ReviewAsync_StructuralReferenceToolsEnabled_RegistersToolsAndKeepsProCursorTools()
    {
        var toolNames = await CaptureToolNamesAsync(true, true);

        Assert.Contains("find_references", toolNames);
        Assert.Contains("get_definition", toolNames);
        // ProCursor knowledge tools remain registered alongside the new structural tools.
        Assert.Contains("ask_procursor_knowledge", toolNames);
        Assert.Contains("get_procursor_symbol_info", toolNames);
    }

    [Fact]
    public async Task ReviewAsync_StructuralReferenceToolsDisabled_OmitsStructuralTools()
    {
        var toolNames = await CaptureToolNamesAsync(false, true);

        Assert.DoesNotContain("find_references", toolNames);
        Assert.DoesNotContain("get_definition", toolNames);
        // Kill-switch only gates the structural tools; ProCursor tools are unaffected.
        Assert.Contains("ask_procursor_knowledge", toolNames);
    }

    private static async Task<List<string>> CaptureToolNamesAsync(bool structuralReferenceToolsEnabled, bool supportsProCursor)
    {
        List<string> toolNames = [];
        var mockClient = Substitute.For<IChatClient>();
        mockClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var tools = callInfo.Arg<ChatOptions?>()?.Tools;
                toolNames = tools is null
                    ? []
                    : tools.OfType<AIFunction>().Select(t => t.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
                return CreateFinalReviewResponse("Done.");
            });

        var options = Microsoft.Extensions.Options.Options.Create(
            new AiReviewOptions
            {
                MaxIterations = 5,
                ConfidenceThreshold = 70,
                ModelId = string.Empty,
                EnableStructuralReferenceTools = structuralReferenceToolsEnabled,
            });

        var tools = Substitute.For<IReviewContextTools, IProCursorAvailabilityAware>();
        ((IProCursorAvailabilityAware)tools).SupportsProCursorTools.Returns(supportsProCursor);

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            options,
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        await sut.ReviewAsync(CreatePullRequest(), CreateContext(tools));
        return toolNames;
    }

    [Fact]
    public async Task ReviewAsync_MixedTextAndToolCall_PreservesLatestTextWithoutForcedFinalCall()
    {
        var mockClient = Substitute.For<IChatClient>();
        var mockTools = Substitute.For<IReviewContextTools>();
        mockTools.GetChangedFilesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChangedFileSummary>>([]));

        var mixedJson = JsonSerializer.Serialize(
            new
            {
                summary = "Provisional review.",
                comments = Array.Empty<object>(),
                confidence_evaluations = new[] { new { concern = "correctness", confidence = 20 } },
                loop_complete = false,
            });

        mockClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                CreateMixedTextAndFunctionCallResponse(mixedJson, "call-1", "get_changed_files", "{}"),
                CreateFunctionCallResponse("call-2", "get_changed_files", "{}"));

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(2),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        var result = await sut.ReviewAsync(CreatePullRequest(), CreateContext(mockTools));

        await mockClient.Received(2)
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());
        Assert.Equal("Provisional review.", result.Summary);
    }

    [Fact]
    public async Task ReviewAsync_RepeatedMixedTextAndToolCall_StopsAfterDuplicateTurn()
    {
        var mockClient = Substitute.For<IChatClient>();
        var mockTools = Substitute.For<IReviewContextTools>();
        mockTools.GetChangedFilesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChangedFileSummary>>([]));

        var repeatedJson = JsonSerializer.Serialize(
            new
            {
                summary = "Still investigating.",
                comments = Array.Empty<object>(),
                confidence_evaluations = new[] { new { concern = "correctness", confidence = 20 } },
                loop_complete = false,
            });

        mockClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                CreateMixedTextAndFunctionCallResponse(repeatedJson, "call-1", "get_changed_files", "{}"),
                CreateMixedTextAndFunctionCallResponse(repeatedJson, "call-1", "get_changed_files", "{}"));

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(10),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        var result = await sut.ReviewAsync(CreatePullRequest(), CreateContext(mockTools));

        await mockClient.Received(2)
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());
        await mockTools.Received(1).GetChangedFilesAsync(Arg.Any<CancellationToken>());
        Assert.Equal("Still investigating.", result.Summary);
    }

    // A repeated assistant turn carrying BOTH text and a tool call breaks the loop with that tool call
    // left unserviced. When the text is not valid final JSON, the schema-repair step appends more messages
    // behind the orphaned call and re-sends the whole transcript. Strict providers (Anthropic via LiteLLM)
    // reject a replay containing a tool_use without a following tool_result, so the orphan must be dropped
    // from every outgoing payload.
    [Fact]
    public async Task ReviewAsync_RepeatedMixedTurnThenRepair_DropsUnansweredToolCallFromReplay()
    {
        var capturedMessages = new List<List<ChatMessage>>();
        var mockClient = Substitute.For<IChatClient>();
        var mockTools = Substitute.For<IReviewContextTools>();
        mockTools.GetChangedFilesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChangedFileSummary>>([]));

        // Non-JSON prose: survives NormalizeJsonPayload but fails final-review parsing, so repair fires.
        const string nonJsonText = "Let me inspect the delimiter handling before finalizing.";

        mockClient
            .GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(messages => capturedMessages.Add(messages.ToList())),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                CreateMixedTextAndFunctionCallResponse(nonJsonText, "call-1", "get_changed_files", "{}"),
                CreateMixedTextAndFunctionCallResponse(nonJsonText, "call-1", "get_changed_files", "{}"),
                CreateFinalReviewResponse("Repaired final review."));

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(10),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        var result = await sut.ReviewAsync(CreatePullRequest(), CreateContext(mockTools));

        // Two loop turns (the second duplicate breaks the loop) plus the schema-repair send.
        Assert.Equal(3, capturedMessages.Count);

        // Every send — including the repair replay that used to carry the orphan mid-transcript — must
        // keep each assistant tool call answered by a tool result in the immediately following message.
        Assert.All(capturedMessages, AssertEveryToolCallIsAnswered);

        Assert.Equal("Repaired final review.", result.Summary);
    }

    private static void AssertEveryToolCallIsAnswered(IReadOnlyList<ChatMessage> messages)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            var calls = messages[i].Contents.OfType<FunctionCallContent>().ToList();
            if (calls.Count == 0)
            {
                continue;
            }

            var answered = i + 1 < messages.Count
                ? messages[i + 1].Contents.OfType<FunctionResultContent>()
                    .Select(result => result.CallId)
                    .ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

            Assert.All(calls, call => Assert.Contains(call.CallId, answered));
        }
    }

    [Fact]
    public async Task ReviewAsync_RepeatedToolOnlyTurn_ForcesFinalReviewAfterDuplicateTurn()
    {
        var mockClient = Substitute.For<IChatClient>();
        var mockTools = Substitute.For<IReviewContextTools>();
        mockTools.GetChangedFilesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChangedFileSummary>>([]));

        mockClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                CreateFunctionCallResponse("call-1", "get_changed_files", "{}"),
                CreateFunctionCallResponse("call-1", "get_changed_files", "{}"),
                CreateFinalReviewResponse("Forced final review."));

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(10),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        var result = await sut.ReviewAsync(CreatePullRequest(), CreateContext(mockTools));

        await mockClient.Received(3)
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());
        await mockTools.Received(1).GetChangedFilesAsync(Arg.Any<CancellationToken>());
        Assert.Equal("Forced final review.", result.Summary);
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
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
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
        Assert.Equal(AgentReviewSessionMode.StatelessReplay, context.LoopMetrics.SessionMode);
    }

    [Fact]
    public async Task ReviewAsync_WithProviderManagedSession_UsesConversationIdAndRecordsSessionMetrics()
    {
        var capturedMessages = new List<List<ChatMessage>>();
        var capturedConversationIds = new List<string?>();
        var mockClient = Substitute.For<IChatClient>();

        var mockTools = Substitute.For<IReviewContextTools>();
        mockTools.GetChangedFilesAsync(Arg.Any<CancellationToken>()).Returns([]);

        var transport = Substitute.For<IManagedReviewSessionTransport>();
        transport
            .GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(messages => capturedMessages.Add(messages.ToList())),
                Arg.Do<ChatOptions>(options => capturedConversationIds.Add(options.ConversationId)),
                Arg.Any<CancellationToken>())
            .Returns(_ => new ChatResponse(
            [
                .. CreateFunctionCallResponse("call-1", "get_changed_files", "{}").Messages,
                new ChatMessage(
                    ChatRole.Tool,
                    [new FunctionResultContent("call-1", "[]")]),
                .. CreateFinalReviewResponse("Done.").Messages,
            ])
            {
                ConversationId = "conv-1",
                ResponseId = "resp-2",
            });

        var transportFactory = Substitute.For<IManagedReviewSessionTransportFactory>();
        transportFactory.Create(mockClient, Arg.Any<IReadOnlyList<AIFunction>>()).Returns(transport);

        var sut = CreateSut(mockClient, transportFactory);

        var context = CreateContext(mockTools);
        context.PerFileHint = new PerFileReviewHint("src/Foo.cs", 1, 1, []);
        context.RuntimeCapabilities = new AgentReviewRuntimeCapabilities(true, true, false, true);

        await sut.ReviewAsync(CreatePullRequest(), context);

        transportFactory.Received(1).Create(mockClient, Arg.Any<IReadOnlyList<AIFunction>>());
        await transport.Received(1)
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions>(),
                Arg.Any<CancellationToken>());
        await mockClient.DidNotReceive()
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());

        Assert.Single(capturedConversationIds);
        Assert.Null(capturedConversationIds[0]);
        Assert.Single(capturedMessages);
        Assert.NotNull(context.ReviewSession);
        Assert.Equal(AgentReviewSessionMode.ProviderManagedSession, context.ReviewSession!.Mode);
        Assert.Equal(AgentReviewPromptMode.InitialBind, context.ReviewSession.ActivePromptMode);
        Assert.Equal("conv-1", context.ReviewSession.RemoteConversationId);
        Assert.Equal("conv-1", context.ReviewSession.ContinuationHandle?.ProviderSessionId);
        Assert.Equal("resp-2", context.ReviewSession.ContinuationHandle?.ProviderResponseId);
        Assert.NotNull(context.LoopMetrics);
        Assert.Equal(AgentReviewSessionMode.ProviderManagedSession, context.LoopMetrics!.SessionMode);
        Assert.Equal(AgentReviewPromptMode.InitialBind, context.LoopMetrics.ActivePromptMode);
        Assert.NotNull(context.LoopMetrics.TurnsJson);
        Assert.Equal("conv-1", context.LoopMetrics.ProviderConversationId);
        Assert.Equal("resp-2", context.LoopMetrics.ProviderResponseId);
    }

    [Fact]
    public async Task ReviewAsync_WithProviderManagedContinuation_DoesNotResendConversationIdAfterBinding()
    {
        var capturedConversationIds = new List<string?>();
        var transport = Substitute.For<IManagedReviewSessionTransport>();
        transport
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Do<ChatOptions>(options => capturedConversationIds.Add(options.ConversationId)),
                Arg.Any<CancellationToken>())
            .Returns(
                _ => new ChatResponse(
                [
                    .. CreateFunctionCallResponse("call-1", "get_changed_files", "{}").Messages,
                    new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "[]")]),
                ])
                {
                    ConversationId = "conv-1",
                    ResponseId = "resp-1",
                },
                _ => new ChatResponse(CreateFinalReviewResponse("Done on continuation.").Messages)
                {
                    ConversationId = "conv-1",
                    ResponseId = "resp-2",
                });

        var mockClient = Substitute.For<IChatClient>();
        var transportFactory = Substitute.For<IManagedReviewSessionTransportFactory>();
        transportFactory.Create(mockClient, Arg.Any<IReadOnlyList<AIFunction>>()).Returns(transport);

        var mockTools = Substitute.For<IReviewContextTools>();
        mockTools.GetChangedFilesAsync(Arg.Any<CancellationToken>()).Returns([]);

        var context = CreateContext(mockTools);
        context.PerFileHint = new PerFileReviewHint("src/Foo.cs", 1, 1, []);
        context.RuntimeCapabilities = new AgentReviewRuntimeCapabilities(true, true, false, true);

        var sut = CreateSut(mockClient, transportFactory, 2);

        await sut.ReviewAsync(CreatePullRequest(), context);

        Assert.Equal(2, capturedConversationIds.Count);
        Assert.Null(capturedConversationIds[0]);
        Assert.Null(capturedConversationIds[1]);
    }

    [Fact]
    public async Task ReviewAsync_PerFileSession_CompactsReplayAndUsesWorkingMemorySummary()
    {
        var capturedMessages = new List<List<ChatMessage>>();
        var mockClient = Substitute.For<IChatClient>();
        mockClient
            .GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(messages => capturedMessages.Add(messages.ToList())),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                CreateFunctionCallResponse("call-1", "get_changed_files", "{}"),
                CreateFinalReviewResponse("Done."));

        var mockTools = Substitute.For<IReviewContextTools>();
        mockTools.GetChangedFilesAsync(Arg.Any<CancellationToken>())
            .Returns([new ChangedFileSummary("src/Foo.cs", ChangeType.Edit)]);

        var file = new ChangedFile("src/Foo.cs", ChangeType.Edit, "code", "+code");
        var pr = new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            1,
            1,
            "PR",
            null,
            "feature/x",
            "main",
            [file]);
        var context = new ReviewSystemContext(null, [], mockTools)
        {
            PerFileHint = new PerFileReviewHint("src/Foo.cs", 1, 1, pr.AllPrFileSummaries),
        };

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        await sut.ReviewAsync(pr, context);

        Assert.Equal(2, capturedMessages.Count);
        var secondTurnMessages = capturedMessages[1];
        Assert.Contains(
            secondTurnMessages,
            message => message.Role == ChatRole.System &&
                       (message.Text?.Contains("Working memory summary for prior bulky context:", StringComparison.Ordinal) ?? false));
        Assert.Contains(
            secondTurnMessages,
            message => message.Role == ChatRole.Assistant && message.Contents.OfType<FunctionCallContent>().Any());
        Assert.Contains(
            secondTurnMessages,
            message => message.Role == ChatRole.Tool);
        Assert.NotNull(context.ReviewSession);
        Assert.Equal(AgentReviewSessionMode.LocalManagedSession, context.ReviewSession!.Mode);
        Assert.NotEmpty(context.ReviewSession.WorkingMemory);
        Assert.NotNull(context.LoopMetrics);
        Assert.Equal(AgentReviewSessionMode.LocalManagedSession, context.LoopMetrics!.SessionMode);
        Assert.Contains("DeltaContext", context.LoopMetrics.TurnsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviewAsync_NonFileByFileProviderManagedSession_StaysOnDirectChatClient()
    {
        var mockClient = Substitute.For<IChatClient>();
        mockClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateFinalReviewResponse("Direct path."));

        var transportFactory = Substitute.For<IManagedReviewSessionTransportFactory>();
        var context = CreateContext();
        context.RuntimeCapabilities = new AgentReviewRuntimeCapabilities(true, true, false, true);

        var sut = CreateSut(mockClient, transportFactory);

        var result = await sut.ReviewAsync(CreatePullRequest(), context);

        Assert.Equal("Direct path.", result.Summary);
        transportFactory.DidNotReceive().Create(Arg.Any<IChatClient>(), Arg.Any<IReadOnlyList<AIFunction>>());
        await mockClient.Received(1)
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());
        Assert.Equal(AgentReviewSessionMode.StatelessReplay, context.ReviewSession!.Mode);
        Assert.Null(context.ReviewSession.RemoteConversationId);
    }

    [Fact]
    public async Task ReviewAsync_ProviderManagedFailure_DowngradesAndRecordsFallback()
    {
        var fallbackMessages = new List<List<ChatMessage>>();
        var mockClient = Substitute.For<IChatClient>();
        mockClient
            .GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(messages => fallbackMessages.Add(messages.ToList())),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => new ChatResponse(CreateFinalReviewResponse("Done after downgrade.").Messages));

        var mockTools = Substitute.For<IReviewContextTools>();
        mockTools.GetChangedFilesAsync(Arg.Any<CancellationToken>()).Returns([]);

        var transport = Substitute.For<IManagedReviewSessionTransport>();
        transport
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(
                _ => new ChatResponse(
                [
                    .. CreateFunctionCallResponse("call-1", "get_changed_files", "{}").Messages,
                    new ChatMessage(
                        ChatRole.Tool,
                        [new FunctionResultContent("call-1", "[]")]),
                ])
                {
                    ConversationId = "conv-1",
                    ResponseId = "resp-1",
                },
                _ => throw new InvalidOperationException("provider continuation failed"));

        var transportFactory = Substitute.For<IManagedReviewSessionTransportFactory>();
        transportFactory.Create(mockClient, Arg.Any<IReadOnlyList<AIFunction>>()).Returns(transport);

        var protocolId = Guid.NewGuid();
        var recorder = Substitute.For<IProtocolRecorder>();
        var context = new ReviewSystemContext(null, [], mockTools)
        {
            ActiveProtocolId = protocolId,
            PerFileHint = new PerFileReviewHint("src/Foo.cs", 1, 1, []),
            ProtocolRecorder = recorder,
            RuntimeCapabilities = new AgentReviewRuntimeCapabilities(true, true, false, true),
        };

        var sut = CreateSut(mockClient, transportFactory);

        await sut.ReviewAsync(CreatePullRequest(), context);

        await recorder.Received()
            .RecordReviewStrategyEventAsync(
                protocolId,
                ReviewProtocolEventNames.ReviewAgentSessionBinding,
                Arg.Is<string?>(json => json != null &&
                                        json.Contains("created_remote_thread", StringComparison.Ordinal) &&
                                        json.Contains("conv-1", StringComparison.Ordinal)),
                Arg.Is<string?>(json => json != null && json.Contains("resp-1", StringComparison.Ordinal)),
                Arg.Is<string?>(value => value == null),
                Arg.Any<CancellationToken>());

        await transport.Received(2)
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions>(),
                Arg.Any<CancellationToken>());
        await mockClient.Received(1)
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());
        Assert.Single(fallbackMessages);
        Assert.Contains(fallbackMessages[0], message => message.Role == ChatRole.Assistant && message.Contents.OfType<FunctionCallContent>().Any());
        Assert.Contains(fallbackMessages[0], message => message.Role == ChatRole.Tool);
        Assert.NotNull(context.ReviewSession);
        Assert.Equal(AgentReviewSessionMode.LocalManagedSession, context.ReviewSession!.Mode);
        Assert.Single(context.ReviewSession.Fallbacks);
        Assert.Equal(AgentReviewSessionMode.ProviderManagedSession, context.ReviewSession.Fallbacks[0].FromMode);
        Assert.Equal(AgentReviewSessionMode.LocalManagedSession, context.ReviewSession.Fallbacks[0].ToMode);
        Assert.NotNull(context.LoopMetrics);
        Assert.NotNull(context.LoopMetrics!.FallbacksJson);
        Assert.Contains("provider_session_continue_failed", context.LoopMetrics.FallbacksJson, StringComparison.Ordinal);
        await recorder.Received()
            .RecordReviewStrategyEventAsync(
                protocolId,
                ReviewProtocolEventNames.ReviewAgentSessionFallback,
                Arg.Is<string?>(json => json != null && json.Contains("provider_session_continue_failed", StringComparison.Ordinal)),
                Arg.Is<string?>(value => value == null),
                Arg.Is<string?>(value => value == null),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_ProviderManagedCreateFailure_DowngradesWithoutBindingToAnotherConversation()
    {
        var fallbackMessages = new List<List<ChatMessage>>();
        var mockClient = Substitute.For<IChatClient>();
        mockClient
            .GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(messages => fallbackMessages.Add(messages.ToList())),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => new ChatResponse(CreateFinalReviewResponse("Done after create failure.").Messages));

        var transport = Substitute.For<IManagedReviewSessionTransport>();
        transport
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<ChatResponse>(new InvalidOperationException("provider session create failed")));

        var transportFactory = Substitute.For<IManagedReviewSessionTransportFactory>();
        transportFactory.Create(mockClient, Arg.Any<IReadOnlyList<AIFunction>>()).Returns(transport);

        var protocolId = Guid.NewGuid();
        var recorder = Substitute.For<IProtocolRecorder>();
        var context = CreateContext();
        context.ActiveProtocolId = protocolId;
        context.PerFileHint = new PerFileReviewHint("src/Foo.cs", 1, 1, []);
        context.ProtocolRecorder = recorder;
        context.RuntimeCapabilities = new AgentReviewRuntimeCapabilities(true, true, false, true);

        var sut = CreateSut(mockClient, transportFactory);

        var result = await sut.ReviewAsync(CreatePullRequest(), context);

        Assert.Equal("Done after create failure.", result.Summary);
        Assert.NotNull(context.ReviewSession);
        Assert.Equal(AgentReviewSessionMode.LocalManagedSession, context.ReviewSession!.Mode);
        Assert.Null(context.ReviewSession.RemoteConversationId);
        Assert.DoesNotContain(context.ReviewSession.Fallbacks, fallback => fallback.Reason == "provider_session_continue_failed");
        Assert.Contains(context.ReviewSession.Fallbacks, fallback => fallback.Reason == "provider_session_create_failed");
        Assert.Single(fallbackMessages);
        await recorder.DidNotReceive()
            .RecordReviewStrategyEventAsync(
                protocolId,
                ReviewProtocolEventNames.ReviewAgentSessionBinding,
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>());
        await recorder.Received()
            .RecordReviewStrategyEventAsync(
                protocolId,
                ReviewProtocolEventNames.ReviewAgentSessionFallback,
                Arg.Is<string?>(json => json != null && json.Contains("provider_session_create_failed", StringComparison.Ordinal)),
                Arg.Is<string?>(value => value == null),
                Arg.Is<string?>(value => value == null),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_ProviderManagedFailure_PreservesRecoveredStateInFallbackRecord()
    {
        var mockClient = Substitute.For<IChatClient>();
        mockClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => new ChatResponse(CreateFinalReviewResponse("Recovered via fallback.").Messages));

        var mockTools = Substitute.For<IReviewContextTools>();
        mockTools.GetChangedFilesAsync(Arg.Any<CancellationToken>()).Returns([]);

        var transport = Substitute.For<IManagedReviewSessionTransport>();
        transport
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(
                _ => new ChatResponse(
                [
                    .. CreateFunctionCallResponse("call-1", "get_changed_files", "{}").Messages,
                    new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "[]")]),
                ])
                {
                    ConversationId = "conv-1",
                    ResponseId = "resp-1",
                },
                _ => throw new InvalidOperationException("provider continuation failed"));

        var transportFactory = Substitute.For<IManagedReviewSessionTransportFactory>();
        transportFactory.Create(mockClient, Arg.Any<IReadOnlyList<AIFunction>>()).Returns(transport);

        var context = new ReviewSystemContext(null, [], mockTools)
        {
            PerFileHint = new PerFileReviewHint("src/Foo.cs", 1, 1, []),
            RuntimeCapabilities = new AgentReviewRuntimeCapabilities(true, true, false, true),
        };

        var sut = CreateSut(mockClient, transportFactory);

        await sut.ReviewAsync(CreatePullRequest(), context);

        var fallback = Assert.Single(context.ReviewSession!.Fallbacks);
        Assert.Equal("provider_session_continue_failed", fallback.Reason);
        Assert.Contains("preserved durable system prompts", fallback.PreservedState, StringComparison.Ordinal);
        Assert.Equal(1, fallback.TurnNumber);
        Assert.Equal(AgentReviewPromptMode.FullReplayFallback, context.ReviewSession.ActivePromptMode);
    }

    [Fact]
    public async Task ReviewAsync_ProviderManagedRepeatedToolOnlyTurn_DowngradesBeforeForcedFinal()
    {
        var capturedConversationIds = new List<string?>();
        var capturedMessages = new List<List<ChatMessage>>();
        var transportConversationIds = new List<string?>();
        var mockClient = Substitute.For<IChatClient>();
        mockClient
            .GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(messages => capturedMessages.Add(messages.ToList())),
                Arg.Do<ChatOptions?>(options => capturedConversationIds.Add(options?.ConversationId)),
                Arg.Any<CancellationToken>())
            .Returns(_ => CreateFinalReviewResponse("Done after forced final."));

        var transport = Substitute.For<IManagedReviewSessionTransport>();
        transport
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Do<ChatOptions>(options => transportConversationIds.Add(options.ConversationId)),
                Arg.Any<CancellationToken>())
            .Returns(
                _ => new ChatResponse(CreateFunctionCallResponse("call-1", "get_changed_files", "{}").Messages)
                {
                    ConversationId = "conv-1",
                    ResponseId = "resp-1",
                },
                _ => new ChatResponse(CreateFunctionCallResponse("call-1", "get_changed_files", "{}").Messages)
                {
                    ConversationId = "conv-1",
                    ResponseId = "resp-2",
                });

        var transportFactory = Substitute.For<IManagedReviewSessionTransportFactory>();
        transportFactory.Create(mockClient, Arg.Any<IReadOnlyList<AIFunction>>()).Returns(transport);

        var mockTools = Substitute.For<IReviewContextTools>();
        mockTools.GetChangedFilesAsync(Arg.Any<CancellationToken>()).Returns([]);

        var context = CreateContext(mockTools);
        context.PerFileHint = new PerFileReviewHint("src/Foo.cs", 1, 1, []);
        context.RuntimeCapabilities = new AgentReviewRuntimeCapabilities(true, true, false, true);

        var sut = CreateSut(mockClient, transportFactory, 2);

        await sut.ReviewAsync(CreatePullRequest(), context);

        Assert.Equal([null, null], transportConversationIds);
        Assert.Single(capturedConversationIds);
        Assert.Null(capturedConversationIds[0]);
        Assert.Equal(AgentReviewSessionMode.LocalManagedSession, context.ReviewSession!.Mode);
        Assert.Equal(AgentReviewPromptMode.FullReplayFallback, context.ReviewSession.ActivePromptMode);
        Assert.Contains(
            context.ReviewSession.Fallbacks,
            fallback => fallback.FromMode == AgentReviewSessionMode.ProviderManagedSession &&
                        fallback.ToMode == AgentReviewSessionMode.LocalManagedSession &&
                        fallback.Reason == "provider_session_forced_final_after_unhandled_tool_call");
        Assert.Contains(
            context.ReviewSession.Fallbacks,
            fallback => fallback.Reason == "provider_session_forced_final_after_unhandled_tool_call");
    }

    [Fact]
    public async Task ReviewAsync_PureJsonWithoutConfidenceEvaluations_BreaksImmediately()
    {
        // Arrange — response without confidence_evaluations field treated as final
        var mockClient = Substitute.For<IChatClient>();
        var json = """{"summary":"Simple review.","comments":[]}""";
        mockClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(10),
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
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
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
                Arg.Is<string?>(input => input != null &&
                                         input.Contains("[system]", StringComparison.Ordinal) &&
                                         input.Contains("[user]", StringComparison.Ordinal)),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_WithCachedUsage_RecordsCacheDiagnosticsAndMetrics()
    {
        var mockClient = Substitute.For<IChatClient>();
        var json = """{"summary":"All good.","comments":[],"loop_complete":true}""";
        mockClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, json))
                {
                    Usage = new UsageDetails { InputTokenCount = 2048, CachedInputTokenCount = 1024, OutputTokenCount = 50 },
                });

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
            PerFileHint = new PerFileReviewHint("src/Foo.cs", 1, 1, []),
            RuntimeCapabilities = new AgentReviewRuntimeCapabilities(false, false, false, false, true, true),
        };

        await sut.ReviewAsync(CreatePullRequest(), context);

        await recorder.Received(1)
            .RecordAiCallAsync(
                protocolId,
                1,
                2048,
                50,
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                1024,
                CacheCallStatus.Hit,
                Arg.Any<string?>(),
                PrefixEligibilityStatus.Eligible,
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>());
        Assert.NotNull(context.LoopMetrics);
        Assert.Equal(1024L, context.LoopMetrics!.TotalCachedInputTokens);
    }

    [Fact]
    public async Task ReviewAsync_WithUnobservableUsage_RecordsUnobservableCacheStatus()
    {
        var mockClient = Substitute.For<IChatClient>();
        var json = """{"summary":"All good.","comments":[],"loop_complete":true}""";
        mockClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, json))
                {
                    Usage = new UsageDetails { InputTokenCount = 2048, OutputTokenCount = 50 },
                });

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
            PerFileHint = new PerFileReviewHint("src/Foo.cs", 1, 1, []),
            RuntimeCapabilities = new AgentReviewRuntimeCapabilities(false, false, false, false, true, true),
        };

        await sut.ReviewAsync(CreatePullRequest(), context);

        await recorder.Received(1)
            .RecordAiCallAsync(
                protocolId,
                1,
                2048,
                50,
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                null,
                CacheCallStatus.Unobservable,
                Arg.Any<string?>(),
                PrefixEligibilityStatus.Eligible,
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>());
    }

    [Fact]
    public async Task ReviewAsync_WithToolOnlyResponseAndEmptyText_RecordsFunctionCallSummary()
    {
        var mockClient = Substitute.For<IChatClient>();
        var mockTools = Substitute.For<IReviewContextTools>();
        mockTools.GetChangedFilesAsync(Arg.Any<CancellationToken>()).Returns([]);

        var toolOnlyResponse = CreateMixedTextAndFunctionCallResponse("", "call-1", "get_changed_files", "{}");
        var finalResponse = CreateFinalReviewResponse("Done.");

        mockClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(toolOnlyResponse, finalResponse);

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

        await sut.ReviewAsync(CreatePullRequest(), context);

        await recorder.Received()
            .RecordAiCallAsync(
                protocolId,
                1,
                Arg.Any<long?>(),
                Arg.Any<long?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                "[tool calls: get_changed_files]",
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
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
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
                Arg.Any<CancellationToken>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<long?>(),
                Arg.Any<long?>(),
                Arg.Any<long?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<IReadOnlyList<ProtocolEventPhaseTiming>?>(),
                Arg.Any<string?>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<bool?>());
    }

    [Fact]
    public async Task ReviewAsync_WithOversizedToolResult_RecordsBoundedToolEvidence()
    {
        var mockClient = Substitute.For<IChatClient>();
        var mockTools = Substitute.For<IReviewContextTools>();
        mockTools.GetFileContentAsync("src/Foo.cs", "source", 1, 200, Arg.Any<CancellationToken>())
            .Returns(new string('x', 5000));

        var toolCallResponse = CreateFunctionCallResponse(
            "call-1",
            "get_file_content",
            """{"path":"src/Foo.cs","branch":"source","startLine":1,"endLine":200}""");
        var finalResponse = CreateFinalReviewResponse("Done.");
        mockClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(toolCallResponse, finalResponse);

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            Microsoft.Extensions.Options.Options.Create(
                new AiReviewOptions
                {
                    MaxIterations = 5,
                    ConfidenceThreshold = 70,
                    MaxToolResultReplayCharacters = 1024,
                }),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        var protocolId = Guid.NewGuid();
        var recorder = Substitute.For<IProtocolRecorder>();
        var context = new ReviewSystemContext(null, [], mockTools)
        {
            ActiveProtocolId = protocolId,
            ProtocolRecorder = recorder,
        };

        await sut.ReviewAsync(CreatePullRequest(), context);

        await recorder.Received(1)
            .RecordToolCallAsync(
                protocolId,
                "get_file_content",
                Arg.Any<string>(),
                Arg.Is<string>(result => result.Length <= 1200 && result.Contains("[Tool evidence bounded", StringComparison.Ordinal)),
                1,
                Arg.Any<CancellationToken>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<long?>(),
                Arg.Any<long?>(),
                Arg.Any<long?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<IReadOnlyList<ProtocolEventPhaseTiming>?>(),
                "Bounded",
                Arg.Is<int?>(tokens => tokens > 1000),
                Arg.Is<int?>(tokens => tokens < 400),
                true);
    }

    [Fact]
    public async Task ReviewAsync_ForcedFinalMalformedJson_RequestsRepairAndReturnsParsedResult()
    {
        var mockClient = Substitute.For<IChatClient>();
        var mockTools = Substitute.For<IReviewContextTools>();
        mockTools.GetChangedFilesAsync(Arg.Any<CancellationToken>()).Returns([]);

        var toolCallResponse = CreateFunctionCallResponse("call-1", "get_changed_files", "{}");
        const string malformedFinal = "{\"summary\":\"Broken \"json\"\",\"comments\":[]";
        const string repairedFinal =
            """
            {"summary":"Broken \"json\"","comments":[],"confidence_evaluations":[],"investigation_complete":true,"loop_complete":true}
            """;

        mockClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                toolCallResponse,
                new ChatResponse(new ChatMessage(ChatRole.Assistant, malformedFinal)),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, repairedFinal)));

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(1),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        var result = await sut.ReviewAsync(CreatePullRequest(), CreateContext(mockTools));

        Assert.Equal("Broken \"json\"", result.Summary);
        await mockClient.Received(3)
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_WithSearchToolCallAndProtocolRecorder_RecordsStructuredSearchResult()
    {
        var mockClient = Substitute.For<IChatClient>();
        var mockTools = Substitute.For<IReviewContextTools>();
        mockTools.SearchSourceRepoAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(
                new RepositorySearchResult(
                    RepositorySearchStatuses.Success,
                    RepositorySearchBranchSides.Source,
                    RepositorySearchPathScopes.Repository,
                    "**/*.cs",
                    [new RepositorySearchMatch("src/Foo.cs", 7, "needle")],
                    [],
                    false));

        var finalJson = """{"summary":"Done.","comments":[]}""";
        var toolCallResponse = new ChatResponse(
            new ChatMessage(
                ChatRole.Assistant,
                [
                    new FunctionCallContent(
                        "call-1",
                        "search_source_repo",
                        new Dictionary<string, object?>
                        {
                            ["searchTerm"] = "needle",
                            ["fileMask"] = "**/*.cs",
                        }),
                ]));
        var finalResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, finalJson));

        mockClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
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

        await sut.ReviewAsync(CreatePullRequest(), context);

        await recorder.Received(1)
            .RecordToolCallAsync(
                protocolId,
                "search_source_repo",
                Arg.Any<string>(),
                Arg.Is<string>(result => string.Equals(result, "[Unknown tool: search_source_repo]", StringComparison.Ordinal)),
                1,
                Arg.Any<CancellationToken>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<long?>(),
                Arg.Any<long?>(),
                Arg.Any<long?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<IReadOnlyList<ProtocolEventPhaseTiming>?>(),
                Arg.Any<string?>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<bool?>());
    }

    [Theory]
    [InlineData(RepositorySearchStatuses.BlockedNotAllowed)]
    [InlineData(RepositorySearchStatuses.BlockedBudgetExhausted)]
    public async Task ReviewAsync_WithBlockedSearchToolCall_PassesStructuredResultToNextTurn(string status)
    {
        var mockClient = Substitute.For<IChatClient>();
        var mockTools = Substitute.For<IReviewContextTools>();
        mockTools.SearchTargetChangedFilesAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(
                new RepositorySearchResult(
                    status,
                    RepositorySearchBranchSides.Target,
                    RepositorySearchPathScopes.ChangedFiles,
                    "**/*.cs",
                    [],
                    [],
                    false));

        var capturedMessages = new List<List<ChatMessage>>();
        var toolCallResponse = new ChatResponse(
            new ChatMessage(
                ChatRole.Assistant,
                [
                    new FunctionCallContent(
                        "call-1",
                        "search_target_changed_files",
                        new Dictionary<string, object?>
                        {
                            ["searchTerm"] = "needle",
                            ["fileMask"] = "**/*.cs",
                        }),
                ]));
        var finalResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"summary":"Done.","comments":[]}"""));

        mockClient
            .GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(messages => capturedMessages.Add(messages.ToList())),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(toolCallResponse, finalResponse);

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        await sut.ReviewAsync(CreatePullRequest(), CreateContext(mockTools));

        Assert.True(capturedMessages.Count >= 2);
        var toolMessage = Assert.Single(capturedMessages[1], message => message.Role == ChatRole.Tool);
        var toolResult = Assert.Single(toolMessage.Contents.OfType<FunctionResultContent>());
        var toolResultJson = Assert.IsType<string>(toolResult.Result);
        using var payload = JsonDocument.Parse(toolResultJson);

        Assert.Equal(status, payload.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "target",
            payload.RootElement.TryGetProperty("branch_side", out var branchSide)
                ? branchSide.GetString()
                : payload.RootElement.GetProperty("branchSide").GetString());
        Assert.Equal(
            "changed_files",
            payload.RootElement.TryGetProperty("path_scope", out var pathScope)
                ? pathScope.GetString()
                : payload.RootElement.GetProperty("pathScope").GetString());
    }

    [Fact]
    public async Task ReviewAsync_WithCodeSearchToolCall_PassesStructuredResultToNextTurn()
    {
        var mockClient = Substitute.For<IChatClient>();
        var mockTools = Substitute.For<IReviewContextTools>();
        mockTools.SearchCodeAsync(Arg.Any<CodeSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new CodeSearchResult(
                    RepositorySearchStatuses.Success,
                    RepositorySearchBranchSides.Source,
                    RepositorySearchPathScopes.Repository,
                    CodeSearchModes.ExactIdentifier,
                    new CodeSearchFilterSet("csharp"),
                    [new CodeSearchMatch("src/Foo.cs", 7, "needle", "csharp", 1, false)],
                    [],
                    false));

        var capturedMessages = new List<List<ChatMessage>>();
        var toolCallResponse = new ChatResponse(
            new ChatMessage(
                ChatRole.Assistant,
                [
                    new FunctionCallContent(
                        "call-1",
                        "search_code",
                        new Dictionary<string, object?>
                        {
                            ["queryText"] = "needle",
                            ["searchMode"] = "exact_identifier",
                            ["branchSide"] = "source",
                            ["pathScope"] = "repository",
                            ["language"] = "csharp",
                            ["fileGlob"] = null,
                            ["pathPrefix"] = null,
                            ["excludeGenerated"] = false,
                            ["excludeTests"] = false,
                        }),
                ]));
        var finalResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"summary":"Done.","comments":[]}"""));

        mockClient
            .GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(messages => capturedMessages.Add(messages.ToList())),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(toolCallResponse, finalResponse);

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        await sut.ReviewAsync(CreatePullRequest(), CreateContext(mockTools));

        var toolMessage = Assert.Single(capturedMessages[1], message => message.Role == ChatRole.Tool);
        var toolResult = Assert.Single(toolMessage.Contents.OfType<FunctionResultContent>());
        var toolResultJson = Assert.IsType<string>(toolResult.Result);
        Assert.Contains("\"searchMode\":\"exact_identifier\"", toolResultJson, StringComparison.Ordinal);
        Assert.Contains("\"filePath\":\"src/Foo.cs\"", toolResultJson, StringComparison.Ordinal);
        await mockTools.Received(1).SearchCodeAsync(
            Arg.Is<CodeSearchRequest>(request => request.SearchMode == CodeSearchModes.ExactIdentifier &&
                                                 request.Filters != null &&
                                                 request.Filters.Language == "csharp"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_WithOverviewAndNeighborhoodTools_RecordsStructuredResults()
    {
        var mockClient = Substitute.For<IChatClient>();
        var mockTools = Substitute.For<IReviewContextTools>();
        mockTools.GetRepositoryOverviewAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(RepositoryOverview.CreateBlocked(RepositorySearchBranchSides.Source, RepositorySearchStatuses.NoMatch));
        mockTools.GetFileNeighborhoodAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                new FileNeighborhood(
                    RepositorySearchStatuses.Success,
                    RepositorySearchBranchSides.Source,
                    "feature/x",
                    "src/Foo.cs",
                    "src/App/App.csproj",
                    [],
                    [],
                    [],
                    [],
                    [],
                    false));

        var finalJson = """{"summary":"Done.","comments":[]}""";
        mockClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        [
                            new FunctionCallContent("call-1", "get_repository_overview", new Dictionary<string, object?> { ["branchSide"] = "source" }),
                            new FunctionCallContent(
                                "call-2", "get_file_neighborhood", new Dictionary<string, object?> { ["filePath"] = "src/Foo.cs", ["branchSide"] = "source" }),
                        ])),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, finalJson)));

        var protocolId = Guid.NewGuid();
        var recorder = Substitute.For<IProtocolRecorder>();
        var context = new ReviewSystemContext(null, [], mockTools)
        {
            ActiveProtocolId = protocolId,
            ProtocolRecorder = recorder,
        };
        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        await sut.ReviewAsync(CreatePullRequest(), context);

        await recorder.Received(1)
            .RecordToolCallAsync(
                protocolId,
                "get_repository_overview",
                Arg.Any<string>(),
                Arg.Is<string>(result => result.Contains("\"status\":\"no_match\"", StringComparison.Ordinal)),
                1,
                Arg.Any<CancellationToken>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<long?>(),
                Arg.Any<long?>(),
                Arg.Any<long?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<IReadOnlyList<ProtocolEventPhaseTiming>?>(),
                Arg.Any<string?>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<bool?>());
        await recorder.Received(1)
            .RecordToolCallAsync(
                protocolId,
                "get_file_neighborhood",
                Arg.Any<string>(),
                Arg.Is<string>(result => result.Contains("\"filePath\":\"src/Foo.cs\"", StringComparison.Ordinal)),
                1,
                Arg.Any<CancellationToken>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<long?>(),
                Arg.Any<long?>(),
                Arg.Any<long?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<IReadOnlyList<ProtocolEventPhaseTiming>?>(),
                Arg.Any<string?>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<bool?>());
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
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
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

        var file = new ChangedFile("src/Foo.cs", ChangeType.Edit, "code", "+code");
        var pr = new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            1,
            1,
            "PR",
            null,
            "feature/x",
            "main",
            new List<ChangedFile> { file }.AsReadOnly());

        var context = new ReviewSystemContext(null, [], mockTools)
        {
            PerFileHint = new PerFileReviewHint("src/Foo.cs", 1, 1, pr.AllPrFileSummaries),
        };

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(10),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        var expectedGlobalSystemPrompt = ReviewPrompts.BuildGlobalSystemPrompt(context);

        // Act
        await sut.ReviewAsync(pr, context);

        // Assert — on iteration 1, two System messages (global + per-file)
        Assert.True(capturedCallArgs.Count >= 2, $"Expected at least 2 AI calls but got {capturedCallArgs.Count}");
        var iter1Messages = capturedCallArgs[0];
        var iter1SystemMsgs = iter1Messages.Where(m => m.Role == ChatRole.System).ToList();
        Assert.Equal(2, iter1SystemMsgs.Count);
        Assert.Contains(expectedGlobalSystemPrompt, iter1SystemMsgs[0].Text ?? "");

        // On iteration 2+, only one System message (per-file context; global dropped)
        var iter2Messages = capturedCallArgs[1];
        var iter2SystemMsgs = iter2Messages.Where(m => m.Role == ChatRole.System).ToList();
        Assert.Equal(2, iter2SystemMsgs.Count);
        Assert.DoesNotContain(expectedGlobalSystemPrompt, iter2SystemMsgs[0].Text ?? "");
        Assert.Contains("src/Foo.cs", iter2SystemMsgs[0].Text ?? "");
        Assert.Contains("Working memory summary for prior bulky context:", iter2SystemMsgs[1].Text ?? "", StringComparison.Ordinal);
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
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(toolCallResponse, finalResponse);

        var mockTools = Substitute.For<IReviewContextTools>();
        mockTools.GetChangedFilesAsync(Arg.Any<CancellationToken>()).Returns([]);

        var file = new ChangedFile("src/Foo.cs", ChangeType.Edit, "code", "+code");
        var pr = new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            1,
            1,
            "PR",
            null,
            "feature/x",
            "main",
            new List<ChangedFile> { file }.AsReadOnly());

        var protocolId = Guid.NewGuid();
        var recorder = Substitute.For<IProtocolRecorder>();
        var context = new ReviewSystemContext(null, [], mockTools)
        {
            PerFileHint = new PerFileReviewHint("src/Foo.cs", 1, 1, pr.AllPrFileSummaries),
            ActiveProtocolId = protocolId,
            ProtocolRecorder = recorder,
        };

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(10),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        // Act
        await sut.ReviewAsync(pr, context);

        // Assert — tool call was on iteration 1
        await recorder.Received(1)
            .RecordToolCallAsync(
                protocolId,
                "get_changed_files",
                Arg.Any<string>(),
                Arg.Any<string>(),
                1,
                Arg.Any<CancellationToken>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<long?>(),
                Arg.Any<long?>(),
                Arg.Any<long?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<IReadOnlyList<ProtocolEventPhaseTiming>?>(),
                Arg.Any<string?>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<bool?>());
    }

    [Fact]
    public async Task ReviewAsync_PerFilePath_SystemPromptUsesSearchCodeContractGuidance()
    {
        string? capturedPerFileSystemPrompt = null;

        var mockClient = Substitute.For<IChatClient>();
        mockClient
            .GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(messages =>
                {
                    capturedPerFileSystemPrompt ??= messages
                        .Where(message => message.Role == ChatRole.System)
                        .Skip(1)
                        .FirstOrDefault()
                        ?.Text;
                }),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateFinalReviewResponse("Done."));

        var file = new ChangedFile("src/Foo.cs", ChangeType.Edit, "code", "+code");
        var pr = new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            1,
            1,
            "PR",
            null,
            "feature/x",
            "main",
            new List<ChangedFile> { file }.AsReadOnly());

        var context = new ReviewSystemContext(null, [], null)
        {
            PerFileHint = new PerFileReviewHint("src/Foo.cs", 1, 1, pr.AllPrFileSummaries),
        };

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(10),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        await sut.ReviewAsync(pr, context);

        Assert.NotNull(capturedPerFileSystemPrompt);
        Assert.Contains("search_code", capturedPerFileSystemPrompt, StringComparison.Ordinal);
        Assert.Contains("related_symbol", capturedPerFileSystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("search_source_repo", capturedPerFileSystemPrompt, StringComparison.Ordinal);
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
                Arg.Do<IEnumerable<ChatMessage>>(msgs => { capturedSystem1 ??= msgs.FirstOrDefault(m => m.Role == ChatRole.System)?.Text; }),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateFinalReviewResponse("Done 1."));

        var mockClient2 = Substitute.For<IChatClient>();
        mockClient2
            .GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(msgs => { capturedSystem2 ??= msgs.FirstOrDefault(m => m.Role == ChatRole.System)?.Text; }),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateFinalReviewResponse("Done 2."));

        var files = new List<ChangedFile>
        {
            new("src/Foo.cs", ChangeType.Edit, "code1", "+code1"),
            new("src/Bar.cs", ChangeType.Edit, "code2", "+code2"),
        }.AsReadOnly();
        var pr = new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            1,
            1,
            "PR",
            null,
            "feature/x",
            "main",
            files);

        var sharedContext = new ReviewSystemContext("Custom system message for client", [], null);

        var context1 = new ReviewSystemContext(
            sharedContext.ClientSystemMessage,
            sharedContext.RepositoryInstructions,
            null)
        {
            PerFileHint = new PerFileReviewHint("src/Foo.cs", 1, 2, pr.AllPrFileSummaries),
        };
        var context2 = new ReviewSystemContext(
            sharedContext.ClientSystemMessage,
            sharedContext.RepositoryInstructions,
            null)
        {
            PerFileHint = new PerFileReviewHint("src/Bar.cs", 2, 2, pr.AllPrFileSummaries),
        };

        var sut1 = new ToolAwareAiReviewCore(
            mockClient1,
            DefaultOptions(),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());
        var sut2 = new ToolAwareAiReviewCore(
            mockClient2,
            DefaultOptions(),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

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
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

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
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

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
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

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
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

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
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

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
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

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
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return Task.FromResult(
                    new ChatResponse(
                        new ChatMessage(
                            ChatRole.Assistant,
                            callCount == 1 ? wrongSchemaJson : correctSchemaJson)));
            });

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

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
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        // Act
        var result = await sut.ReviewAsync(CreatePullRequest(), CreateContext());

        // Assert — exactly one AI call; empty comments array is valid, no correction needed
        await mockClient.Received(1)
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());
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
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        // Act — must not throw
        var result = await sut.ReviewAsync(CreatePullRequest(), CreateContext());

        Assert.Equal("Fence stripped.", result.Summary);
    }

    [Fact]
    public async Task ReviewAsync_ConcatenatedJsonObjects_UsesFirstValidObject()
    {
        var mockClient = Substitute.For<IChatClient>();
        var json = "{" +
                   "\"summary\":\"First object wins.\",\"comments\":[],\"loop_complete\":true" +
                   "}{\"summary\":\"Second object ignored.\",\"comments\":[],\"loop_complete\":true}";
        mockClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        var result = await sut.ReviewAsync(CreatePullRequest(), CreateContext());

        Assert.Equal("First object wins.", result.Summary);
    }

    // ─── T036: MaxIterationsOverride ────────────────────────────────────────────

    [Fact]
    public async Task ReviewAsync_MaxIterationsOverrideOnHint_OverridesGlobalMaxIterations()
    {
        // Arrange — AI always returns low confidence (loop never self-terminates)
        var mockClient = Substitute.For<IChatClient>();
        var callCount = 0;
        mockClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                var lowConfidenceJson = JsonSerializer.Serialize(
                    new
                    {
                        summary = $"Still investigating {callCount}.",
                        comments = Array.Empty<object>(),
                        confidence_evaluations = new[]
                        {
                            new { concern = "security", confidence = 30 },
                        },
                        loop_complete = false,
                    });
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, lowConfidenceJson));
            });

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(10), // global max = 10
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        // PerFileHint.MaxIterationsOverride = 3 → loop must stop at 3, not 10
        var context = new ReviewSystemContext(null, [], null)
        {
            PerFileHint = new PerFileReviewHint(
                "src/BigService.cs",
                1,
                1,
                [])
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

    private sealed class DisabledProCursorReviewTools : IReviewContextTools, IProCursorAvailabilityAware
    {
        public bool SupportsProCursorTools => false;

        public Task<IReadOnlyList<ChangedFileSummary>> GetChangedFilesAsync(CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<ChangedFileSummary>>([]);
        }

        public Task<IReadOnlyList<string>> GetFileTreeAsync(string branch, CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        public Task<string> GetFileContentAsync(string path, string branch, int startLine, int endLine, CancellationToken ct)
        {
            return Task.FromResult(string.Empty);
        }

        public Task<RepositorySearchResult> SearchSourceRepoAsync(string searchTerm, string? fileMask, CancellationToken ct)
        {
            return Task.FromResult(
                new RepositorySearchResult(
                    RepositorySearchStatuses.NoMatch,
                    RepositorySearchBranchSides.Source,
                    RepositorySearchPathScopes.Repository,
                    fileMask,
                    [],
                    [],
                    false));
        }

        public Task<RepositorySearchResult> SearchSourceChangedFilesAsync(string searchTerm, string? fileMask, CancellationToken ct)
        {
            return Task.FromResult(
                new RepositorySearchResult(
                    RepositorySearchStatuses.NoMatch,
                    RepositorySearchBranchSides.Source,
                    RepositorySearchPathScopes.ChangedFiles,
                    fileMask,
                    [],
                    [],
                    false));
        }

        public Task<RepositorySearchResult> SearchTargetRepoAsync(string searchTerm, string? fileMask, CancellationToken ct)
        {
            return Task.FromResult(
                new RepositorySearchResult(
                    RepositorySearchStatuses.NoMatch,
                    RepositorySearchBranchSides.Target,
                    RepositorySearchPathScopes.Repository,
                    fileMask,
                    [],
                    [],
                    false));
        }

        public Task<RepositorySearchResult> SearchTargetChangedFilesAsync(string searchTerm, string? fileMask, CancellationToken ct)
        {
            return Task.FromResult(
                new RepositorySearchResult(
                    RepositorySearchStatuses.NoMatch,
                    RepositorySearchBranchSides.Target,
                    RepositorySearchPathScopes.ChangedFiles,
                    fileMask,
                    [],
                    [],
                    false));
        }

        public Task<CodeSearchResult> SearchCodeAsync(CodeSearchRequest request, CancellationToken ct)
        {
            return Task.FromResult(
                new CodeSearchResult(
                    RepositorySearchStatuses.NoMatch,
                    request.BranchSide,
                    request.PathScope,
                    request.SearchMode,
                    request.Filters,
                    [],
                    [],
                    false));
        }

        public Task<PathSearchResult> SearchPathsAsync(PathSearchRequest request, CancellationToken ct)
        {
            return Task.FromResult(
                new PathSearchResult(
                    RepositorySearchStatuses.NoMatch,
                    request.BranchSide,
                    request.PathScope,
                    request.MatchMode,
                    request.Filters,
                    [],
                    [],
                    false));
        }

        public Task<RepositoryOverview> GetRepositoryOverviewAsync(string branchSide, CancellationToken ct)
        {
            return Task.FromResult(RepositoryOverview.CreateBlocked(branchSide, RepositorySearchStatuses.NoMatch));
        }

        public Task<FileNeighborhood> GetFileNeighborhoodAsync(string filePath, string branchSide, CancellationToken ct)
        {
            return Task.FromResult(FileNeighborhood.CreateBlocked(branchSide, filePath, RepositorySearchStatuses.NoMatch));
        }

        public Task<ProCursorKnowledgeAnswerDto> AskProCursorKnowledgeAsync(string question, CancellationToken ct)
        {
            throw new NotSupportedException();
        }

        public Task<ProCursorSymbolInsightDto> GetProCursorSymbolInfoAsync(
            string symbol,
            string? queryMode,
            int? maxRelations,
            CancellationToken ct)
        {
            throw new NotSupportedException();
        }

        public Task<ReferenceLookupResult> FindReferencesAsync(SymbolReferenceQuery query, CancellationToken ct)
        {
            return Task.FromResult(ReferenceLookupResult.UnavailableResult);
        }

        public Task<DefinitionLookupResult> GetDefinitionAsync(SymbolReferenceQuery query, CancellationToken ct)
        {
            return Task.FromResult(DefinitionLookupResult.UnavailableResult);
        }

        public Task<LinkedItemDetails?> GetLinkedItemDetailsAsync(string providerKey, CancellationToken ct)
        {
            return Task.FromResult<LinkedItemDetails?>(null);
        }

        public Task<IReadOnlyList<LinkedItemComment>> GetLinkedItemDiscussionAsync(string providerKey, CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<LinkedItemComment>>([]);
        }

        public Task<LinkedItem?> ResolveLinkedItemAsync(string relatedTargetKey, CancellationToken ct)
        {
            return Task.FromResult<LinkedItem?>(null);
        }
    }
}

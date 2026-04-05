// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>
///     Integration tests for the get_procursor_symbol_info tool wiring in <see cref="ToolAwareAiReviewCore" />.
/// </summary>
public sealed class ToolAwareAiReviewCoreSymbolToolTests
{
    [Fact]
    public async Task ReviewAsync_WithGetProCursorSymbolInfoTool_DispatchesToReviewContextTools()
    {
        var mockClient = Substitute.For<IChatClient>();
        var mockTools = Substitute.For<IReviewContextTools>();
        mockTools.GetProCursorSymbolInfoAsync("Greeter", "qualifiedName", 10, Arg.Any<CancellationToken>())
            .Returns(new ProCursorSymbolInsightDto(
                "complete",
                Guid.NewGuid(),
                true,
                true,
                new ProCursorSymbolMatchDto(
                    "T:Demo.Greeter",
                    "Greeter",
                    "type",
                    "csharp",
                    "Demo.Greeter",
                    new ProCursorSourceLocationDto("src/Greeter.cs", 3, 12)),
                [],
                "fresh"));

        var callCount = 0;
        mockClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return Task.FromResult(CreateFunctionCallResponse(
                        "call-symbol-1",
                        "get_procursor_symbol_info",
                        "{\"symbol\":\"Greeter\",\"queryMode\":\"qualifiedName\",\"maxRelations\":10}"));
                }

                return Task.FromResult(CreateFinalReviewResponse("Symbol grounded review."));
            });

        var sut = new ToolAwareAiReviewCore(
            mockClient,
            DefaultOptions(maxIterations: 5),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

        var result = await sut.ReviewAsync(CreatePullRequest(), CreateContext(mockTools));

        Assert.Equal(2, callCount);
        Assert.Equal("Symbol grounded review.", result.Summary);
        await mockTools.Received(1).GetProCursorSymbolInfoAsync("Greeter", "qualifiedName", 10, Arg.Any<CancellationToken>());
    }

    private static IOptions<AiReviewOptions> DefaultOptions(int maxIterations = 5)
    {
        return Microsoft.Extensions.Options.Options.Create(
            new AiReviewOptions
            {
                MaxIterations = maxIterations,
                ConfidenceThreshold = 70,
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

    private static ChatResponse CreateFinalReviewResponse(string summary)
    {
        var json = JsonSerializer.Serialize(
            new
            {
                summary,
                comments = Array.Empty<object>(),
                confidence_evaluations = new[]
                {
                    new { concern = "code_quality", confidence = 90 },
                },
                loop_complete = true,
            });
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, json));
    }

    private static ChatResponse CreateFunctionCallResponse(string callId, string functionName, string argsJson)
    {
        var functionCall = new FunctionCallContent(
            callId,
            functionName,
            JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson));
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, [functionCall]));
    }
}

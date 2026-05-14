// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AI;

public sealed class QualityFilterExecutorTests
{
    [Fact]
    public void ParseResponse_WithInvalidAnchorsAndBlankMessages_NormalizesAndSkips()
    {
        const string json = """
                            {
                              "comments": [
                                { "file_path": "src/Foo.cs", "line_number": -4, "severity": "info", "message": "Anchored loosely." },
                                { "file_path": "src/Foo.cs", "line_number": 10, "severity": "warning", "message": "   " }
                              ]
                            }
                            """;

        var comments = QualityFilterExecutor.ParseResponse(json);

        var comment = Assert.Single(comments);
        Assert.Equal("src/Foo.cs", comment.FilePath);
        Assert.Null(comment.LineNumber);
        Assert.Equal(CommentSeverity.Info, comment.Severity);
    }

    [Fact]
    public async Task ApplyAsync_WhenParsedCommentsAreEmpty_FallsBackToOriginalComments()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"comments\":[]}")));

        var sut = CreateSut();
        var original = new List<ReviewComment>
        {
            new("src/A.cs", 1, CommentSeverity.Warning, "Original warning."),
        };

        var result = await sut.ApplyAsync(Guid.NewGuid(), original, new ReviewSystemContext(null, [], null), client, CancellationToken.None);

        Assert.Same(original, result);
    }

    [Fact]
    public async Task ApplyAsync_UsesContextModelIdAndReturnsFilteredComments()
    {
        string? observedModelId = null;
        float? observedTemperature = null;
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var options = callInfo.Arg<ChatOptions?>();
                observedModelId = options?.ModelId;
                observedTemperature = options?.Temperature;
                return new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        "{\"comments\":[{\"file_path\":\"src/Foo.cs\",\"line_number\":4,\"severity\":\"error\",\"message\":\"Filtered issue.\"}]}"));
            });

        var sut = CreateSut();
        var context = new ReviewSystemContext(null, [], null)
        {
            ModelId = "quality-filter-model",
            Temperature = 0.18f,
        };

        var result = await sut.ApplyAsync(
            Guid.NewGuid(),
            [new ReviewComment("src/Foo.cs", 1, CommentSeverity.Warning, "Original warning.")],
            context,
            client,
            CancellationToken.None);

        var comment = Assert.Single(result);
        Assert.Equal("Filtered issue.", comment.Message);
        Assert.Equal(CommentSeverity.Error, comment.Severity);
        Assert.Equal("quality-filter-model", observedModelId);
        Assert.Equal(0.18f, observedTemperature);
    }

    [Fact]
    public async Task ApplyAsync_WhenAiThrows_ReturnsOriginalComments()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns<Task<ChatResponse>>(_ => throw new HttpRequestException("quality filter unavailable"));

        var sut = CreateSut();
        var original = new List<ReviewComment>
        {
            new("src/A.cs", 1, CommentSeverity.Warning, "Original warning."),
            new("src/B.cs", 2, CommentSeverity.Error, "Original error."),
        };

        var result = await sut.ApplyAsync(Guid.NewGuid(), original, new ReviewSystemContext(null, [], null), client, CancellationToken.None);

        Assert.Same(original, result);
    }

    private static QualityFilterExecutor CreateSut()
    {
        return new QualityFilterExecutor(
            new AiReviewOptions { ModelId = "fallback-model" },
            Substitute.For<ILogger<FileByFileReviewOrchestrator>>());
    }
}

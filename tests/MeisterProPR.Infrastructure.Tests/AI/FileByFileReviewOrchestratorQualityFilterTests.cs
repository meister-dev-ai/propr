// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>
///     Unit tests for the cross-file quality-filter AI pass (IMP-08):
///     <list type="bullet">
///         <item>T021 — <c>ParseQualityFilterResponse</c> parsing logic</item>
///         <item>T022 — <c>RunQualityFilterAsync</c> fallback when AI throws</item>
///     </list>
/// </summary>
public class FileByFileReviewOrchestratorQualityFilterTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static FileByFileReviewOrchestrator BuildOrchestrator(IChatClient? effectiveClient = null)
    {
        return new FileByFileReviewOrchestrator(
            Substitute.For<IAiReviewCore>(),
            Substitute.For<IProtocolRecorder>(),
            Substitute.For<IJobRepository>(),
            effectiveClient ?? Substitute.For<IChatClient>(),
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions()),
            Substitute.For<ILogger<FileByFileReviewOrchestrator>>());
    }

    // ── T021: ParseQualityFilterResponse ─────────────────────────────────────────

    [Fact]
    public void ParseQualityFilterResponse_ValidJsonWithTwoComments_ReturnsBoth()
    {
        const string json = """
                            {
                              "comments": [
                                { "file_path": "src/Foo.cs", "line_number": 10, "severity": "error", "message": "Null ref here" },
                                { "file_path": "src/Bar.cs", "line_number": null, "severity": "warning", "message": "Unused variable" }
                              ]
                            }
                            """;

        var result = FileByFileReviewOrchestrator.ParseQualityFilterResponse(json);

        Assert.Equal(2, result.Count);
        Assert.Equal("src/Foo.cs", result[0].FilePath);
        Assert.Equal(CommentSeverity.Error, result[0].Severity);
        Assert.Equal("Null ref here", result[0].Message);
        Assert.Equal("src/Bar.cs", result[1].FilePath);
        Assert.Null(result[1].LineNumber);
        Assert.Equal(CommentSeverity.Warning, result[1].Severity);
    }

    [Fact]
    public void ParseQualityFilterResponse_EmptyCommentsArray_ReturnsEmptyList()
    {
        const string json = """{ "comments": [] }""";

        var result = FileByFileReviewOrchestrator.ParseQualityFilterResponse(json);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseQualityFilterResponse_InvalidJson_ReturnsEmptyList()
    {
        var result = FileByFileReviewOrchestrator.ParseQualityFilterResponse("not valid json {{{}");

        Assert.Empty(result);
    }

    [Fact]
    public void ParseQualityFilterResponse_NullOrWhitespace_ReturnsEmptyList()
    {
        Assert.Empty(FileByFileReviewOrchestrator.ParseQualityFilterResponse(string.Empty));
        Assert.Empty(FileByFileReviewOrchestrator.ParseQualityFilterResponse("   "));
    }

    [Fact]
    public void ParseQualityFilterResponse_UnknownSeverity_DefaultsToWarning()
    {
        const string json = """
                            {
                              "comments": [
                                { "file_path": null, "line_number": null, "severity": "unknown_value", "message": "Some issue" }
                              ]
                            }
                            """;

        var result = FileByFileReviewOrchestrator.ParseQualityFilterResponse(json);

        Assert.Single(result);
        Assert.Equal(CommentSeverity.Warning, result[0].Severity);
    }

    [Fact]
    public void ParseQualityFilterResponse_SuggestionSeverity_ParsedCorrectly()
    {
        const string json = """
                            {
                              "comments": [
                                { "file_path": "src/X.cs", "line_number": 5, "severity": "suggestion", "message": "Consider caching" }
                              ]
                            }
                            """;

        var result = FileByFileReviewOrchestrator.ParseQualityFilterResponse(json);

        Assert.Single(result);
        Assert.Equal(CommentSeverity.Suggestion, result[0].Severity);
    }

    [Fact]
    public void ParseQualityFilterResponse_MissingMessageField_SkipsComment()
    {
        const string json = """
                            {
                              "comments": [
                                { "file_path": "src/X.cs", "line_number": 1, "severity": "warning" }
                              ]
                            }
                            """;

        // A comment with no message is skipped
        var result = FileByFileReviewOrchestrator.ParseQualityFilterResponse(json);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseQualityFilterResponse_NullFilePath_ParsedAsNull()
    {
        const string json = """
                            {
                              "comments": [
                                { "file_path": null, "line_number": null, "severity": "error", "message": "PR-level issue" }
                              ]
                            }
                            """;

        var result = FileByFileReviewOrchestrator.ParseQualityFilterResponse(json);

        Assert.Single(result);
        Assert.Null(result[0].FilePath);
    }

    [Fact]
    public void ParseQualityFilterResponse_LineNumberZero_NormalizesToNull()
    {
        const string json = """
                            {
                              "comments": [
                                { "file_path": "src/Foo.cs", "line_number": 0, "severity": "warning", "message": "Bad inline anchor" }
                              ]
                            }
                            """;

        var result = FileByFileReviewOrchestrator.ParseQualityFilterResponse(json);

        var comment = Assert.Single(result);
        Assert.Equal("src/Foo.cs", comment.FilePath);
        Assert.Null(comment.LineNumber);
    }

    // ── T022: RunQualityFilterAsync fallback behavior ────────────────────────────

    [Fact]
    public async Task RunQualityFilterAsync_WhenAiThrows_ReturnsOriginalComments()
    {
        var throwingClient = Substitute.For<IChatClient>();
        throwingClient
            .GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var sut = BuildOrchestrator();

        var original = new List<ReviewComment>
        {
            new("src/A.cs", 1, CommentSeverity.Warning, "Some warning"),
            new("src/B.cs", 2, CommentSeverity.Error, "Some error"),
        };

        var result = await sut.RunQualityFilterAsync(
            Guid.NewGuid(),
            original,
            new ReviewSystemContext(null, [], null),
            throwingClient,
            CancellationToken.None);

        // Fallback: original comments returned unchanged
        Assert.Equal(2, result.Count);
        Assert.Equal("Some warning", result[0].Message);
        Assert.Equal("Some error", result[1].Message);
    }

    [Fact]
    public async Task RunQualityFilterAsync_UsesContextModelId_WhenProvided()
    {
        string? observedModelId = null;
        float? observedTemperature = null;
        var client = Substitute.For<IChatClient>();
        client
            .GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                observedModelId = callInfo.Arg<ChatOptions?>()?.ModelId;
                observedTemperature = callInfo.Arg<ChatOptions?>()?.Temperature;
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"comments\":[]}"));
            });

        var sut = BuildOrchestrator(client);
        var context = new ReviewSystemContext(null, [], null)
        {
            ModelId = "quality-filter-deployment",
            Temperature = 0.18f,
        };

        var result = await sut.RunQualityFilterAsync(
            Guid.NewGuid(),
            [new ReviewComment("src/A.cs", 1, CommentSeverity.Warning, "Some warning")],
            context,
            client,
            CancellationToken.None);

        Assert.Equal("quality-filter-deployment", observedModelId);
        Assert.Equal(0.18f, observedTemperature);
        Assert.Single(result);
        Assert.Equal("Some warning", result[0].Message);
    }
}

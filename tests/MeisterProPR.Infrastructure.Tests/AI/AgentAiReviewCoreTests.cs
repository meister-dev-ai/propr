using System.Text.Json;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AI;

public class AgentAiReviewCoreTests
{
    private static ChatResponse CreateChatResponse(string content)
    {
        var message = new ChatMessage(ChatRole.Assistant, content);
        return new ChatResponse(message);
    }

    private static PullRequest CreatePullRequest(IReadOnlyList<ChangedFile>? files = null)
    {
        return new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            1,
            1,
            "Test PR",
            "desc",
            "feature/x",
            "main",
            files ?? new List<ChangedFile>().AsReadOnly());
    }

    [Fact]
    public async Task ReviewAsync_AllSeverityLevels_ParsedCorrectly()
    {
        // Arrange
        var json = """
                   {
                     "summary": "All severities.",
                     "comments": [
                       { "filePath": null, "lineNumber": null, "severity": "info", "message": "info msg" },
                       { "filePath": null, "lineNumber": null, "severity": "warning", "message": "warning msg" },
                       { "filePath": null, "lineNumber": null, "severity": "error", "message": "error msg" },
                       { "filePath": null, "lineNumber": null, "severity": "suggestion", "message": "suggestion msg" }
                     ]
                   }
                   """;

        var mockClient = Substitute.For<IChatClient>();
        mockClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(CreateChatResponse(json));

        var core = new AgentAiReviewCore(mockClient);

        // Act
        var result = await core.ReviewAsync(CreatePullRequest());

        // Assert
        Assert.Equal(CommentSeverity.Info, result.Comments[0].Severity);
        Assert.Equal(CommentSeverity.Warning, result.Comments[1].Severity);
        Assert.Equal(CommentSeverity.Error, result.Comments[2].Severity);
        Assert.Equal(CommentSeverity.Suggestion, result.Comments[3].Severity);
    }

    [Fact]
    public async Task ReviewAsync_CancellationTokenForwardedToChatClient()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        var capturedToken = CancellationToken.None;

        var mockClient = Substitute.For<IChatClient>();
        mockClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Do<CancellationToken>(ct => capturedToken = ct))
            .Returns(CreateChatResponse("""{"summary":"ok","comments":[]}"""));

        var core = new AgentAiReviewCore(mockClient);

        // Act
        await core.ReviewAsync(CreatePullRequest(), cts.Token);

        // Assert - the cancellation token was forwarded
        Assert.Equal(cts.Token, capturedToken);
    }

    [Fact]
    public async Task ReviewAsync_EmptyComments_ReturnsEmptyList()
    {
        // Arrange
        var json = """{"summary": "No issues found.", "comments": []}""";

        var mockClient = Substitute.For<IChatClient>();
        mockClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(CreateChatResponse(json));

        var core = new AgentAiReviewCore(mockClient);

        // Act
        var result = await core.ReviewAsync(CreatePullRequest());

        // Assert
        Assert.Equal("No issues found.", result.Summary);
        Assert.Empty(result.Comments);
    }

    [Fact]
    public async Task ReviewAsync_MalformedJsonResponse_ThrowsJsonException()
    {
        // Arrange
        var mockClient = Substitute.For<IChatClient>();
        mockClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(CreateChatResponse("this is not valid json {{ }}"));

        var core = new AgentAiReviewCore(mockClient);

        // Act & Assert
        await Assert.ThrowsAsync<JsonException>(() => core.ReviewAsync(CreatePullRequest()));
    }

    [Fact]
    public async Task ReviewAsync_SendsSystemAndUserMessages()
    {
        // Arrange
        List<ChatMessage>? capturedMessages = null;
        var mockClient = Substitute.For<IChatClient>();
        mockClient
            .GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(msgs => capturedMessages = msgs.ToList()),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateChatResponse("""{"summary":"ok","comments":[]}"""));

        var core = new AgentAiReviewCore(mockClient);

        // Act
        await core.ReviewAsync(CreatePullRequest());

        // Assert - both system and user messages sent
        Assert.NotNull(capturedMessages);
        Assert.Equal(2, capturedMessages!.Count);
        Assert.Equal(ChatRole.System, capturedMessages[0].Role);
        Assert.Equal(ChatRole.User, capturedMessages[1].Role);
    }

    [Fact]
    public async Task ReviewAsync_UnknownSeverity_DefaultsToInfo()
    {
        // Arrange
        var json = """
                   {
                     "summary": "Review done.",
                     "comments": [
                       { "filePath": null, "lineNumber": null, "severity": "unknown_value", "message": "Some comment." }
                     ]
                   }
                   """;

        var mockClient = Substitute.For<IChatClient>();
        mockClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(CreateChatResponse(json));

        var core = new AgentAiReviewCore(mockClient);

        // Act
        var result = await core.ReviewAsync(CreatePullRequest());

        // Assert - unknown severity defaults to Info
        Assert.Equal(CommentSeverity.Info, result.Comments[0].Severity);
    }

    [Fact]
    public async Task ReviewAsync_ValidJsonResponse_ParsesReviewResult()
    {
        // Arrange
        var json = """
                   {
                     "summary": "Overall the PR looks good.",
                     "comments": [
                       { "filePath": "/src/MyFile.cs", "lineNumber": 42, "severity": "warning", "message": "Consider using var here." },
                       { "filePath": null, "lineNumber": null, "severity": "info", "message": "Good PR structure." }
                     ]
                   }
                   """;

        var mockClient = Substitute.For<IChatClient>();
        mockClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(CreateChatResponse(json));

        var core = new AgentAiReviewCore(mockClient);

        // Act
        var result = await core.ReviewAsync(CreatePullRequest());

        // Assert
        Assert.Equal("Overall the PR looks good.", result.Summary);
        Assert.Equal(2, result.Comments.Count);
        Assert.Equal("/src/MyFile.cs", result.Comments[0].FilePath);
        Assert.Equal(42, result.Comments[0].LineNumber);
        Assert.Equal(CommentSeverity.Warning, result.Comments[0].Severity);
        Assert.Equal("Consider using var here.", result.Comments[0].Message);
        Assert.Null(result.Comments[1].FilePath);
        Assert.Equal(CommentSeverity.Info, result.Comments[1].Severity);
    }
}
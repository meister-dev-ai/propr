using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>Unit tests for <see cref="AgentMentionAnswerService" />.</summary>
public sealed class AgentMentionAnswerServiceTests
{
    private static readonly Guid BotGuid = new("0CAEB875-08D2-6D69-88FB-302B06D21993");

    private static IChatClient MakeChatClient(string reply = "The answer.")
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        return chatClient;
    }

    private static PullRequest MakePr(IReadOnlyList<PrCommentThread>? threads = null)
    {
        return new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            1,
            1,
            "My PR",
            null,
            "feat/x",
            "main",
            [],
            ExistingThreads: threads);
    }

    [Fact]
    public async Task AnswerAsync_StripsMentionGuidPrefix_BeforePassingQuestionToAI()
    {
        // Arrange
        var captured = new List<IEnumerable<ChatMessage>>();
        var chatClient = Substitute.For<IChatClient>();
        chatClient
            .GetResponseAsync(Arg.Do<IEnumerable<ChatMessage>>(m => captured.Add(m)), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        var sut = new AgentMentionAnswerService(chatClient, NullLogger<AgentMentionAnswerService>.Instance);
        var rawMention = $"@<{BotGuid}> Is this method safe?";

        // Act
        await sut.AnswerAsync(MakePr(), rawMention, 5);

        // Assert: the user message must contain the cleaned question, not the raw GUID prefix
        var userMessage = captured.Single().Single(m => m.Role == ChatRole.User).Text!;
        Assert.Contains("Is this method safe?", userMessage);
        Assert.DoesNotContain(BotGuid.ToString(), userMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnswerAsync_WhenThreadHasFileAndLine_IncludesLocationInPrompt()
    {
        // Arrange
        var captured = new List<IEnumerable<ChatMessage>>();
        var chatClient = Substitute.For<IChatClient>();
        chatClient
            .GetResponseAsync(Arg.Do<IEnumerable<ChatMessage>>(m => captured.Add(m)), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        var thread = new PrCommentThread(
            5,
            "src/Foo.cs",
            42,
            [
                new PrThreadComment("alice", $"@<{BotGuid}> Is this safe?", BotGuid),
            ]);
        var pr = MakePr(threads: [thread]);
        var sut = new AgentMentionAnswerService(chatClient, NullLogger<AgentMentionAnswerService>.Instance);

        // Act
        await sut.AnswerAsync(pr, $"@<{BotGuid}> Is this safe?", 5);

        // Assert: location info is present in the user message
        var userMessage = captured.Single().Single(m => m.Role == ChatRole.User).Text!;
        Assert.Contains("src/Foo.cs", userMessage);
        Assert.Contains("L42", userMessage);
    }

    [Fact]
    public async Task AnswerAsync_WhenThreadIdNotFound_StillSendsQuestionWithoutCrashing()
    {
        // Arrange
        var chatClient = MakeChatClient("fine");
        var sut = new AgentMentionAnswerService(chatClient, NullLogger<AgentMentionAnswerService>.Instance);

        // Act & Assert: no exception, returns AI text
        var result = await sut.AnswerAsync(MakePr(), $"@<{BotGuid}> Hello?", 999);
        Assert.Equal("fine", result);
    }

    [Fact]
    public async Task AnswerAsync_ReturnsAIResponseText()
    {
        // Arrange
        var chatClient = MakeChatClient("Certainly, here is the answer.");
        var sut = new AgentMentionAnswerService(chatClient, NullLogger<AgentMentionAnswerService>.Instance);

        // Act
        var result = await sut.AnswerAsync(MakePr(), "any question", 1);

        // Assert
        Assert.Equal("Certainly, here is the answer.", result);
    }
}

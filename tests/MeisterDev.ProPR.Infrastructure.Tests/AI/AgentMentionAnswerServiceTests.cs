// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using MeisterDev.ProPR.TestSupport;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

/// <summary>Unit tests for <see cref="AgentMentionAnswerService" />.</summary>
public sealed class AgentMentionAnswerServiceTests
{
    private static readonly Guid BotGuid = new("0CAEB875-08D2-6D69-88FB-302B06D21993");
    private static readonly Guid ClientId = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444");

    private static AiConnectionDto BuildActiveConnection()
    {
        return AiConnectionTestFactory.CreateChatConnection(
            ClientId,
            baseUrl: "https://ai.example.com",
            secret: "test-key");
    }

    private static AgentMentionAnswerService CreateSut(IChatClient chatClient, IClientRegistry? clientRegistry = null)
    {
        var aiConnectionRepository = Substitute.For<IAiConnectionRepository>();
        aiConnectionRepository.GetActiveForClientAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(BuildActiveConnection());

        var aiChatClientFactory = Substitute.For<IAiChatClientFactory>();
        aiChatClientFactory.CreateClient(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(chatClient);

        return new AgentMentionAnswerService(
            aiConnectionRepository,
            aiChatClientFactory,
            NullLogger<AgentMentionAnswerService>.Instance,
            clientRegistry: clientRegistry);
    }

    private static IChatClient MakeChatClient(string reply = "The answer.")
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        return chatClient;
    }

    private static PullRequest MakePr(IReadOnlyList<PrCommentThread>? threads = null)
    {
        return new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
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
            .GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(m => captured.Add(m)),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        var sut = CreateSut(chatClient);
        var rawMention = $"@<{BotGuid}> Is this method safe?";

        // Act
        await sut.AnswerAsync(MakePr(), ClientId, rawMention, "5");

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
            .GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(m => captured.Add(m)),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        var thread = new PrCommentThread(
            "5",
            "src/Foo.cs",
            42,
            [
                new PrThreadComment("alice", $"@<{BotGuid}> Is this safe?", BotGuid),
            ]);
        var pr = MakePr([thread]);
        var sut = CreateSut(chatClient);

        // Act
        await sut.AnswerAsync(pr, ClientId, $"@<{BotGuid}> Is this safe?", "5");

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
        var sut = CreateSut(chatClient);

        // Act & Assert: no exception, returns AI text
        var result = await sut.AnswerAsync(MakePr(), ClientId, $"@<{BotGuid}> Hello?", "999");
        Assert.Equal("fine", result.Text);
    }

    [Fact]
    public async Task AnswerAsync_ReturnsAIResponseText()
    {
        // Arrange
        var chatClient = MakeChatClient("Certainly, here is the answer.");
        var sut = CreateSut(chatClient);

        // Act
        var result = await sut.AnswerAsync(MakePr(), ClientId, "any question", "1");

        // Assert
        Assert.Equal("Certainly, here is the answer.", result.Text);
    }

    [Fact]
    public async Task AnswerAsync_CarriesBackWhatTheCallSpent()
    {
        // The provider reports the tokens on the same response as the text. Returning the text alone is what
        // left mention spend unmetered.
        var chatClient = Substitute.For<IChatClient>();
        chatClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "The answer."))
                {
                    Usage = new UsageDetails { InputTokenCount = 1_234, OutputTokenCount = 56 },
                });
        var sut = CreateSut(chatClient);

        var result = await sut.AnswerAsync(MakePr(), ClientId, "any question", "1");

        Assert.Equal(1_234, result.Usage.InputTokens);
        Assert.Equal(56, result.Usage.OutputTokens);
        Assert.False(result.Usage.IsEstimated);
    }

    [Fact]
    public async Task AnswerAsync_ProviderReportsNoUsage_FlagsTheCountsEstimated()
    {
        // Zeros that were never measured must not read as a free call.
        var sut = CreateSut(MakeChatClient("The answer."));

        var result = await sut.AnswerAsync(MakePr(), ClientId, "any question", "1");

        Assert.True(result.Usage.IsEstimated);
    }

    [Fact]
    public async Task AnswerAsync_WithConfiguredOutputLanguage_StatesItInTheSystemPrompt()
    {
        var captured = new List<IEnumerable<ChatMessage>>();
        var chatClient = Substitute.For<IChatClient>();
        chatClient
            .GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(m => captured.Add(m)),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        var clientRegistry = Substitute.For<IClientRegistry>();
        clientRegistry.GetOutputLanguageAsync(ClientId, Arg.Any<CancellationToken>()).Returns("de");
        var sut = CreateSut(chatClient, clientRegistry);

        await sut.AnswerAsync(MakePr(), ClientId, "Is this safe?", "5");

        var systemMessage = captured.Single().Single(m => m.Role == ChatRole.System).Text!;
        Assert.Contains("`de`", systemMessage, StringComparison.Ordinal);
    }
}

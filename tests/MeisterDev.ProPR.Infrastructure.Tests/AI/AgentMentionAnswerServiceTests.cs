// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
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
    [Fact]
    public async Task AnswerAsync_PrioritizesReferencedFileAndLaterHunkWithinBounds()
    {
        var prefix = "@@ -1,250 +1,250 @@\n" + string.Join('\n', Enumerable.Range(1, 250).Select(i => $" line-{i}"));
        var relevant = new ChangedFile("docs/Deploy.md", ChangeType.Edit, "", prefix + "\n@@ -500,2 +500,2 @@\n deployment-context\n+relevant-change");
        var files = Enumerable.Range(1, 12).Select(i => new ChangedFile($"file-{i}", ChangeType.Edit, "", "+other")).Append(relevant).ToArray();
        var pr = MakePr([new PrCommentThread("5", "/docs/Deploy.md", 501, [])]) with { ChangedFiles = files };
        var prompt = await CapturePromptAsync(pr, "5");

        Assert.Contains("relevant-change", prompt);
        Assert.Contains("deployment-context", prompt);
        Assert.Equal(10, prompt.Split("=== ").Length - 1);
        Assert.True(prompt.IndexOf("=== docs/Deploy.md", StringComparison.Ordinal) < prompt.IndexOf("=== file-1", StringComparison.Ordinal));
        Assert.DoesNotContain("line-1\n", prompt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnswerAsync_UsesDeletedLineContextAtEarlyAndLateHunks(bool lateHunk)
    {
        var prefix = lateHunk
            ? "@@ -1,300 +1,300 @@\n" + string.Join('\n', Enumerable.Range(1, 300).Select(i => $" prefix-{i}")) + "\n"
            : "";
        var oldLine = lateHunk ? 900 : 40;
        var newLine = lateHunk ? 301 : 2;
        var focus = new ChangedFile(
            "src/Deleted.cs", ChangeType.Edit, "",
            prefix + $"@@ -{oldLine},1 +{newLine},0 @@\n-deleted-focus");
        var files = Enumerable.Range(1, 12)
            .Select(i => new ChangedFile($"file-{i}", ChangeType.Edit, "", "+other"))
            .Append(focus).ToArray();
        var pr = MakePr([new PrCommentThread("5", "/src/Deleted.cs", oldLine, [])]) with { ChangedFiles = files };

        var prompt = await CapturePromptAsync(pr, "5");

        Assert.Contains("-deleted-focus", prompt);
        Assert.DoesNotContain("Referenced line is unavailable", prompt);
        Assert.Equal(10, prompt.Split("=== ").Length - 1);
        Assert.StartsWith("src/Deleted.cs", prompt.Split("=== ")[1]);
        var focusDiffRows = prompt.Split("=== ")[1].Split('\n')
            .Count(line => line.StartsWith(' ') || line.StartsWith('+') || line.StartsWith('-') || line.StartsWith("@@", StringComparison.Ordinal));
        Assert.InRange(focusDiffRows, 1, 200);
    }

    [Fact]
    public async Task AnswerAsync_PrefersUpdatedLineContextOverAnEarlierDeletedLine()
    {
        var removed = Enumerable.Range(1, 500).Select(i => i == 450 ? "-removed-focus" : $"-removed-{i}");
        var diff = "@@ -1,500 +0,0 @@\n" + string.Join('\n', removed)
                                         + "\n@@ -950,0 +450,1 @@\n+updated-focus";
        var pr = MakePr([new PrCommentThread("5", "src/Changed.cs", 450, [])]) with
        {
            ChangedFiles = [new ChangedFile("src/Changed.cs", ChangeType.Edit, "", diff)],
        };

        var prompt = await CapturePromptAsync(pr, "5");

        Assert.Contains("+updated-focus", prompt);
        Assert.DoesNotContain("-removed-focus", prompt);
        Assert.DoesNotContain("Referenced line is unavailable", prompt);
    }

    [Theory]
    [InlineData("docs/Deploy.md", "@@ -999999999999999999999,1 +1,0 @@\n-deleted", 450, "Referenced line is unavailable")]
    [InlineData("docs/Deploy.md", "@@ -1 +999999999999999999999 @@\n+first", 1, "Referenced line is unavailable")]
    [InlineData("docs/Deploy.md", "", 12, "Referenced file diff is unavailable")]
    [InlineData("docs/Deploy.md", "@@ -1 +1 @@\n+first", 500, "Referenced line is unavailable")]
    [InlineData("docs/deploy.md", "@@ -1 +1 @@\n+first", 1, "Referenced file context is unavailable")]
    public async Task AnswerAsync_ReportsUnavailableReferencedContext(string path, string diff, int line, string expected)
    {
        var pr = MakePr([new PrCommentThread("5", path, line, [])]) with
        {
            ChangedFiles = [new ChangedFile("docs/Deploy.md", ChangeType.Edit, "", diff)],
        };
        Assert.Contains(expected, await CapturePromptAsync(pr, "5"));
    }

    [Fact]
    public async Task AnswerAsync_ReportsMissingThreadAndSupportsPrLevelQuestions()
    {
        Assert.Contains("Question thread context is unavailable", await CapturePromptAsync(MakePr(), "missing"));
        var prompt = await CapturePromptAsync(MakePr([new PrCommentThread("5", null, null, [])]), "5");
        Assert.Contains("(PR-level)", prompt);
        Assert.DoesNotContain("Referenced file context is unavailable", prompt);
    }

    [Fact]
    public async Task AnswerAsync_BoundsAndLabelsAnExcerptInsideALongHunk()
    {
        var diff = "@@ -1,600 +1,600 @@\n" + string.Join('\n', Enumerable.Range(1, 600).Select(i => $" line-{i}"));
        var pr = MakePr([new PrCommentThread("5", "large.cs", 450, [])]) with
        {
            ChangedFiles = [new ChangedFile("large.cs", ChangeType.Edit, "", diff)],
        };
        var prompt = await CapturePromptAsync(pr, "5");
        Assert.Contains("line-450", prompt);
        Assert.Contains("Diff excerpt omits", prompt);
        var renderedDiff = prompt.Split('\n')
            .Count(line => line.StartsWith(" line-", StringComparison.Ordinal) || line.StartsWith("@@", StringComparison.Ordinal));
        Assert.InRange(renderedDiff, 1, 200);
    }

    private static async Task<string> CapturePromptAsync(PullRequest pr, string threadId)
    {
        string? prompt = null;
        var client = MakeChatClient();
        client.GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(messages => prompt = messages.Single(m => m.Role == ChatRole.User).Text),
                Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        await CreateSut(client).AnswerAsync(pr, ClientId, "Explain this change", threadId);
        return Assert.IsType<string>(prompt);
    }

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
        var connection = BuildActiveConnection();
        var runtime = Substitute.For<IResolvedAiChatRuntime>();
        runtime.ChatClient.Returns(chatClient);
        runtime.Connection.Returns(connection);
        runtime.Model.Returns(connection.ConfiguredModels[0]);

        var aiRuntimeResolver = Substitute.For<IAiRuntimeResolver>();
        aiRuntimeResolver.ResolveChatRuntimeAsync(ClientId, AiPurpose.ReviewDefault, Arg.Any<CancellationToken>())
            .Returns(runtime);

        return new AgentMentionAnswerService(
            aiRuntimeResolver,
            NullLogger<AgentMentionAnswerService>.Instance,
            clientRegistry);
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

    // A mention answer never asks the model for its reasoning and never records model output that could
    // carry it: the request carries no provider reasoning options, and the answer is the assistant text,
    // which excludes reasoning parts. There is therefore no tenant capture decision to apply here.
    [Fact]
    public async Task AnswerAsync_AsksForNoReasoningAndKeepsNoneTheProviderReturns()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        [
                            new TextReasoningContent("the model weighing the question privately"),
                            new TextContent("Yes, the method checks its arguments."),
                        ])));

        var sut = CreateSut(chatClient);

        var answer = await sut.AnswerAsync(MakePr(), ClientId, $"@<{BotGuid}> Is this method safe?", "5");

        Assert.Equal("Yes, the method checks its arguments.", answer.Text);
        await chatClient.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Is<ChatOptions?>(options => options != null && options.RawRepresentationFactory == null),
            Arg.Any<CancellationToken>());
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

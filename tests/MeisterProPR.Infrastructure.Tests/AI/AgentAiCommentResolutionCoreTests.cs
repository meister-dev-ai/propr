// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Options;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>
///     Unit tests for <see cref="AgentAiCommentResolutionCore" />.
///     The <see cref="IChatClient" /> is substituted to avoid real AI calls.
/// </summary>
public sealed class AgentAiCommentResolutionCoreTests
{
    private const string ModelId = "gpt-4o";

    private static PrCommentThread BuildThread(int threadId, params (string author, string content, Guid? authorId)[] comments)
    {
        var prComments = comments
            .Select(c => new PrThreadComment(c.author, c.content, c.authorId))
            .ToList()
            .AsReadOnly();
        return new PrCommentThread(threadId, "/src/Foo.cs", 10, prComments);
    }

    private static PullRequest BuildPr()
    {
        return new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            1,
            2,
            "Fix null-ref bug",
            null,
            "feature/fix",
            "main",
            new List<ChangedFile>().AsReadOnly());
    }

    private static IChatClient BuildChatClient(string jsonResponse)
    {
        var client = Substitute.For<IChatClient>();
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, jsonResponse)]);
        client.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(response);
        return client;
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_WhenAiReturnsResolved_ReturnsIsResolvedTrue()
    {
        var chatClient = BuildChatClient("""{"resolved": true, "replyText": "Fixed in latest commit."}""");
        var sut = new AgentAiCommentResolutionCore();
        var thread = BuildThread(1, ("Bot", "Null reference on line 10.", null));
        var pr = BuildPr();

        var result = await sut.EvaluateCodeChangeAsync(thread, pr, chatClient, ModelId);

        Assert.True(result.IsResolved);
        Assert.Equal("Fixed in latest commit.", result.ReplyText);
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_WhenAiReturnsUnresolved_ReturnsIsResolvedFalse()
    {
        var chatClient = BuildChatClient("""{"resolved": false, "replyText": null}""");
        var sut = new AgentAiCommentResolutionCore();
        var thread = BuildThread(1, ("Bot", "Potential race condition.", null));
        var pr = BuildPr();

        var result = await sut.EvaluateCodeChangeAsync(thread, pr, chatClient, ModelId);

        Assert.False(result.IsResolved);
        Assert.Null(result.ReplyText);
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_WhenAiIsUncertain_ReturnsIsResolvedFalse()
    {
        // T022: AI must return unresolved when unsure rather than guessing resolved
        var chatClient = BuildChatClient("""{"resolved": false, "replyText": "I'm not sure if this was fully addressed."}""");
        var sut = new AgentAiCommentResolutionCore();
        var thread = BuildThread(1, ("Bot", "Consider edge case.", null));
        var pr = BuildPr();

        var result = await sut.EvaluateCodeChangeAsync(thread, pr, chatClient, ModelId);

        Assert.False(result.IsResolved);
    }

    [Fact]
    public async Task EvaluateConversationalReplyAsync_ReturnsReplyText()
    {
        var chatClient = BuildChatClient("""{"resolved": false, "replyText": "Great question! This is intentional because..."}""");
        var sut = new AgentAiCommentResolutionCore();
        var thread = BuildThread(
            1,
            ("Bot", "Consider using async here.", null),
            ("Dev", "Why async specifically?", null));

        var result = await sut.EvaluateConversationalReplyAsync(thread, chatClient, ModelId);

        Assert.False(result.IsResolved);
        Assert.NotNull(result.ReplyText);
        Assert.Contains("Great question", result.ReplyText);
    }

    [Fact]
    public async Task EvaluateConversationalReplyAsync_WhenResolved_ReturnsReplyTextWithReasoning()
    {
        // Resolved always carries reasoning explaining why the thread is being closed.
        var chatClient = BuildChatClient("""{"resolved": true, "replyText": "Closing — the null-guard on line 12 addresses my concern."}""");
        var sut = new AgentAiCommentResolutionCore();
        var thread = BuildThread(
            1,
            ("Bot", "Missing null check.", null),
            ("Dev", "Added the null check in latest commit.", null));

        var result = await sut.EvaluateConversationalReplyAsync(thread, chatClient, ModelId);

        Assert.True(result.IsResolved);
        Assert.NotNull(result.ReplyText);
        Assert.Contains("Closing", result.ReplyText);
    }

    [Fact]
    public async Task EvaluateConversationalReplyAsync_WhenNotResolvedAndNothingToAdd_ReturnsNullReplyText()
    {
        // Not resolved + nothing important to say → replyText is null, no unnecessary noise.
        var chatClient = BuildChatClient("""{"resolved": false, "replyText": null}""");
        var sut = new AgentAiCommentResolutionCore();
        var thread = BuildThread(
            1,
            ("Bot", "Please refactor this method.", null),
            ("Dev", "Will do in next commit.", null));

        var result = await sut.EvaluateConversationalReplyAsync(thread, chatClient, ModelId);

        Assert.False(result.IsResolved);
        Assert.Null(result.ReplyText);
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_SendsThreadAndDiffContext_ToChatClient()
    {
        var chatClient = BuildChatClient("""{"resolved": true, "replyText": null}""");
        var sut = new AgentAiCommentResolutionCore();
        var thread = BuildThread(1, ("Bot", "Missing null check on line 10.", null));
        var pr = BuildPr();

        await sut.EvaluateCodeChangeAsync(thread, pr, chatClient, ModelId);

        await chatClient.Received(1)
            .GetResponseAsync(
                Arg.Is<IList<ChatMessage>>(msgs => msgs.Any(m => m.Text != null && m.Text.Contains("Missing null check"))),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateConversationalReplyAsync_SendsThreadHistory_ToChatClient()
    {
        var chatClient = BuildChatClient("""{"resolved": false, "replyText": "Because of X."}""");
        var sut = new AgentAiCommentResolutionCore();
        var thread = BuildThread(
            1,
            ("Bot", "Use StringBuilder here.", null),
            ("Dev", "Why StringBuilder?", null));

        await sut.EvaluateConversationalReplyAsync(thread, chatClient, ModelId);

        await chatClient.Received(1)
            .GetResponseAsync(
                Arg.Is<IList<ChatMessage>>(msgs => msgs.Any(m => m.Text != null && m.Text.Contains("Why StringBuilder"))),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_OnlyIncludesMatchingFileDiff_NotOtherFiles()
    {
        // Arrange: PR with two changed files; thread anchored to only one of them.
        var chatClient = BuildChatClient("""{"resolved": true, "replyText": null}""");
        var sut = new AgentAiCommentResolutionCore();

        var comments = new List<PrThreadComment> { new("Bot", "Null check missing.", null) }.AsReadOnly();
        var thread = new PrCommentThread(1, "/src/Target.cs", 5, comments);

        var targetFile = new ChangedFile("/src/Target.cs", Domain.Enums.ChangeType.Edit, "", "diff for target");
        var otherFile = new ChangedFile("/src/Other.cs", Domain.Enums.ChangeType.Edit, "", "diff for other");
        var pr = new PullRequest(
            "https://dev.azure.com/org", "proj", "repo", "repo", 1, 2,
            "Fix", null, "feature/fix", "main",
            new List<ChangedFile> { targetFile, otherFile }.AsReadOnly());

        await sut.EvaluateCodeChangeAsync(thread, pr, chatClient, ModelId);

        await chatClient.Received(1).GetResponseAsync(
            Arg.Is<IList<ChatMessage>>(msgs =>
                msgs.Any(m => m.Text != null && m.Text.Contains("diff for target")) &&
                msgs.All(m => m.Text == null || !m.Text.Contains("diff for other"))),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_WhenFileNotInChangedFiles_SendsNotChangedMessage()
    {
        var chatClient = BuildChatClient("""{"resolved": false, "replyText": null}""");
        var sut = new AgentAiCommentResolutionCore();

        var comments = new List<PrThreadComment> { new("Bot", "Issue here.", null) }.AsReadOnly();
        var thread = new PrCommentThread(1, "/src/Missing.cs", 1, comments);

        var otherFile = new ChangedFile("/src/Other.cs", Domain.Enums.ChangeType.Edit, "", "diff for other");
        var pr = new PullRequest(
            "https://dev.azure.com/org", "proj", "repo", "repo", 1, 2,
            "Fix", null, "feature/fix", "main",
            new List<ChangedFile> { otherFile }.AsReadOnly());

        await sut.EvaluateCodeChangeAsync(thread, pr, chatClient, ModelId);

        await chatClient.Received(1).GetResponseAsync(
            Arg.Is<IList<ChatMessage>>(msgs =>
                msgs.Any(m => m.Text != null && m.Text.Contains("not changed in the latest iteration")) &&
                msgs.All(m => m.Text == null || !m.Text.Contains("diff for other"))),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_PrLevelThread_SendsFileListWithoutDiffs()
    {
        var chatClient = BuildChatClient("""{"resolved": false, "replyText": null}""");
        var sut = new AgentAiCommentResolutionCore();

        // PR-level thread: FilePath is null
        var comments = new List<PrThreadComment> { new("Bot", "Overall design concern.", null) }.AsReadOnly();
        var thread = new PrCommentThread(1, null, null, comments);

        var fileA = new ChangedFile("/src/A.cs", Domain.Enums.ChangeType.Edit, "", "big diff A");
        var fileB = new ChangedFile("/src/B.cs", Domain.Enums.ChangeType.Add, "", "big diff B");
        var pr = new PullRequest(
            "https://dev.azure.com/org", "proj", "repo", "repo", 1, 2,
            "Fix", null, "feature/fix", "main",
            new List<ChangedFile> { fileA, fileB }.AsReadOnly());

        await sut.EvaluateCodeChangeAsync(thread, pr, chatClient, ModelId);

        await chatClient.Received(1).GetResponseAsync(
            Arg.Is<IList<ChatMessage>>(msgs =>
                msgs.Any(m => m.Text != null && m.Text.Contains("/src/A.cs")) &&
                msgs.Any(m => m.Text != null && m.Text.Contains("/src/B.cs")) &&
                msgs.All(m => m.Text == null || !m.Text.Contains("big diff A")) &&
                msgs.All(m => m.Text == null || !m.Text.Contains("big diff B"))),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>());
    }
}

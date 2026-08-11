// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.AI;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

/// <summary>
///     Unit tests for <see cref="AgentAiCommentResolutionCore" />.
///     The <see cref="IChatClient" /> is substituted to avoid real AI calls.
/// </summary>
public sealed class AgentAiCommentResolutionCoreTests
{
    private const string ModelId = "gpt-4o";

    private static PrCommentThread BuildThread(
        string threadId,
        params (string author, string content, Guid? authorId)[] comments)
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

    /// <summary>Returns the next response on each successive call, so a two-round evaluation can be driven.</summary>
    private static IChatClient BuildChatClient(string firstResponse, string secondResponse)
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse([new ChatMessage(ChatRole.Assistant, firstResponse)]),
                new ChatResponse([new ChatMessage(ChatRole.Assistant, secondResponse)]));
        return client;
    }

    /// <summary>
    ///     A pull request whose thread anchor file is the only diff loaded, with a manifest naming a second
    ///     changed file. This is the shape the thread pass produces.
    /// </summary>
    private static PullRequest BuildPrWithManifest()
    {
        var anchorFile = new ChangedFile("/src/Target.cs", ChangeType.Edit, "", "diff for target");
        return new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            1,
            2,
            "Fix",
            null,
            "feature/fix",
            "main",
            new List<ChangedFile> { anchorFile }.AsReadOnly(),
            PrStatus.Active,
            null,
            new List<ChangedFileSummary>
            {
                new("src/Target.cs", ChangeType.Edit),
                new("src/Service.cs", ChangeType.Edit),
            }.AsReadOnly());
    }

    private static PrCommentThread BuildAnchoredThread()
    {
        var comments = new List<PrThreadComment> { new("Bot", "The implementation does not validate these values.") }
            .AsReadOnly();
        return new PrCommentThread("1", "/src/Target.cs", 5, comments);
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_WhenTheFixLandedInAnotherFile_AsksForThatFileAndResolves()
    {
        var chatClient = BuildChatClient(
            """{"resolved": false, "replyText": null, "needFiles": ["src/Service.cs"]}""",
            """{"resolved": true, "replyText": "The service now validates its arguments."}""");
        var fetched = new List<string>();
        var evidence = new ThreadEvidenceAccess((path, _) =>
        {
            fetched.Add(path);
            return Task.FromResult<ChangedFile?>(new ChangedFile(path, ChangeType.Edit, "", "diff for the service"));
        });
        var sut = new AgentAiCommentResolutionCore();

        var result = await sut.EvaluateCodeChangeAsync(
            BuildAnchoredThread(),
            BuildPrWithManifest(),
            chatClient,
            ModelId,
            CancellationToken.None,
            null,
            false,
            evidence);

        Assert.True(result.IsResolved);
        Assert.Equal(2, result.ModelCallCount);
        Assert.Equal(["src/Service.cs"], fetched);
        await chatClient.Received(1)
            .GetResponseAsync(
                Arg.Is<IList<ChatMessage>>(msgs =>
                    msgs.Any(m => m.Text != null && m.Text.Contains("diff for the service"))),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_RequestedPathOutsideTheManifest_IsNeverFetched()
    {
        var chatClient = BuildChatClient(
            """{"resolved": false, "replyText": null, "needFiles": ["src/Secrets.cs", "/etc/passwd"]}""",
            """{"resolved": true, "replyText": "unreachable"}""");
        var fetched = new List<string>();
        var evidence = new ThreadEvidenceAccess((path, _) =>
        {
            fetched.Add(path);
            return Task.FromResult<ChangedFile?>(new ChangedFile(path, ChangeType.Edit, "", "secret diff"));
        });
        var sut = new AgentAiCommentResolutionCore();

        var result = await sut.EvaluateCodeChangeAsync(
            BuildAnchoredThread(),
            BuildPrWithManifest(),
            chatClient,
            ModelId,
            CancellationToken.None,
            null,
            false,
            evidence);

        Assert.Empty(fetched);
        Assert.False(result.IsResolved);
        Assert.Equal(1, result.ModelCallCount);
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_RequestedPathIsMatchedAcrossTheLeadingSlash()
    {
        // Azure DevOps anchors a thread to a repo-root-absolute path while the manifest holds the
        // repo-relative one, so a request in either form has to resolve to the same file. What reaches the
        // fetcher is the manifest's own path, never the model-generated string.
        var chatClient = BuildChatClient(
            """{"resolved": false, "replyText": null, "needFiles": ["/src/Service.cs"]}""",
            """{"resolved": true, "replyText": "Validated."}""");
        var fetched = new List<string>();
        var evidence = new ThreadEvidenceAccess((path, _) =>
        {
            fetched.Add(path);
            return Task.FromResult<ChangedFile?>(new ChangedFile(path, ChangeType.Edit, "", "diff for service"));
        });
        var sut = new AgentAiCommentResolutionCore();

        var result = await sut.EvaluateCodeChangeAsync(
            BuildAnchoredThread(),
            BuildPrWithManifest(),
            chatClient,
            ModelId,
            CancellationToken.None,
            null,
            false,
            evidence);

        Assert.Equal(["src/Service.cs"], fetched);
        Assert.True(result.IsResolved);
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_WhenTheSecondCallFails_KeepsTheFirstRoundsAnswer()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                _ => new ChatResponse(
                [
                    new ChatMessage(
                        ChatRole.Assistant,
                        """{"resolved": false, "replyText": "Claimed in a file I was not given.", "needFiles": ["src/Service.cs"]}"""),
                ]),
                _ => throw new InvalidOperationException("the provider is down"));
        var evidence = new ThreadEvidenceAccess((path, _) => Task.FromResult<ChangedFile?>(new ChangedFile(path, ChangeType.Edit, "", "a diff")));
        var sut = new AgentAiCommentResolutionCore();

        var result = await sut.EvaluateCodeChangeAsync(
            BuildAnchoredThread(),
            BuildPrWithManifest(),
            chatClient,
            ModelId,
            CancellationToken.None,
            null,
            false,
            evidence);

        Assert.Equal("Claimed in a file I was not given.", result.ReplyText);
        Assert.Equal(1, result.ModelCallCount);
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_WhenTheSecondAnswerIsUnreadable_FallsBackToTheFirst()
    {
        var chatClient = BuildChatClient(
            """{"resolved": false, "replyText": "Still missing.", "needFiles": ["src/Service.cs"]}""",
            "I could not produce JSON for this one.");
        var evidence = new ThreadEvidenceAccess((path, _) => Task.FromResult<ChangedFile?>(new ChangedFile(path, ChangeType.Edit, "", "a diff")));
        var sut = new AgentAiCommentResolutionCore();

        var result = await sut.EvaluateCodeChangeAsync(
            BuildAnchoredThread(),
            BuildPrWithManifest(),
            chatClient,
            ModelId,
            CancellationToken.None,
            null,
            false,
            evidence);

        Assert.Equal("Still missing.", result.ReplyText);

        // Both were spent, so both are billable, even though only the first produced the result.
        Assert.Equal(2, result.ModelCallCount);
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_WhenTheVerdictAlreadyResolved_DoesNotBuyASecondCall()
    {
        var chatClient = BuildChatClient(
            """{"resolved": true, "replyText": "Fixed.", "needFiles": ["src/Service.cs"]}""",
            """{"resolved": false, "replyText": "unreachable"}""");
        var evidence = new ThreadEvidenceAccess((path, _) => Task.FromResult<ChangedFile?>(new ChangedFile(path, ChangeType.Edit, "", "a diff")));
        var sut = new AgentAiCommentResolutionCore();

        var result = await sut.EvaluateCodeChangeAsync(
            BuildAnchoredThread(),
            BuildPrWithManifest(),
            chatClient,
            ModelId,
            CancellationToken.None,
            null,
            false,
            evidence);

        Assert.True(result.IsResolved);
        Assert.Equal(1, result.ModelCallCount);
    }

    [Theory]
    [InlineData("""{"resolved": true, "replyText": "Fixed.", "needFiles": "src/Service.cs"}""")]
    [InlineData("""{"resolved": true, "replyText": "Fixed.", "needFiles": [{"path": "src/Service.cs"}]}""")]
    [InlineData("""{"resolved": true, "replyText": "Fixed.", "needFiles": 7}""")]
    public async Task EvaluateCodeChangeAsync_WhenTheRequestIsTheWrongShape_TheVerdictStillStands(string json)
    {
        // A request that cannot be parsed is discarded. The verdict alongside it is retained.
        var chatClient = BuildChatClient(json);
        var evidence = new ThreadEvidenceAccess((path, _) => Task.FromResult<ChangedFile?>(new ChangedFile(path, ChangeType.Edit, "", "a diff")));
        var sut = new AgentAiCommentResolutionCore();

        var result = await sut.EvaluateCodeChangeAsync(
            BuildAnchoredThread(),
            BuildPrWithManifest(),
            chatClient,
            ModelId,
            CancellationToken.None,
            null,
            false,
            evidence);

        Assert.True(result.IsResolved);
        Assert.Equal("Fixed.", result.ReplyText);
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_WhenTheRequestedFileHasNoReadableDiff_DoesNotBuyASecondCall()
    {
        // A binary file is returned non-null with an empty diff. Treating it as evidence would spend a call
        // presenting a heading with nothing beneath it.
        var chatClient = BuildChatClient(
            """{"resolved": false, "replyText": "Not yet.", "needFiles": ["src/Service.cs"]}""",
            """{"resolved": true, "replyText": "unreachable"}""");
        var evidence = new ThreadEvidenceAccess((path, _) => Task.FromResult<ChangedFile?>(new ChangedFile(path, ChangeType.Edit, "", "", true)));
        var sut = new AgentAiCommentResolutionCore();

        var result = await sut.EvaluateCodeChangeAsync(
            BuildAnchoredThread(),
            BuildPrWithManifest(),
            chatClient,
            ModelId,
            CancellationToken.None,
            null,
            false,
            evidence);

        Assert.Equal(1, result.ModelCallCount);
        Assert.False(result.IsResolved);
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_ManyChangedFiles_ListsABoundedNumberAndSaysSo()
    {
        var chatClient = BuildChatClient("""{"resolved": false, "replyText": null}""");
        var summaries = Enumerable.Range(0, 200)
            .Select(index => new ChangedFileSummary($"src/File{index}.cs", ChangeType.Edit))
            .ToList()
            .AsReadOnly();
        var pr = BuildPrWithManifest() with { AllChangedFileSummaries = summaries };
        var sut = new AgentAiCommentResolutionCore();

        await sut.EvaluateCodeChangeAsync(BuildAnchoredThread(), pr, chatClient, ModelId);

        await chatClient.Received(1)
            .GetResponseAsync(
                Arg.Is<IList<ChatMessage>>(msgs =>
                    msgs.Any(m => m.Text != null && m.Text.Contains("further changed files, not listed here"))
                    && msgs.All(m => m.Text == null || !m.Text.Contains("src/File199.cs"))),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_WhenNoOtherFileChanged_LeavesTheAskingRulesOut()
    {
        // No file can be requested, so rules describing a list of other files would describe an absent
        // list.
        var chatClient = BuildChatClient("""{"resolved": false, "replyText": null}""");
        var pr = BuildPrWithManifest() with
        {
            AllChangedFileSummaries = new List<ChangedFileSummary> { new("src/Target.cs", ChangeType.Edit) }
                .AsReadOnly(),
        };
        var evidence = new ThreadEvidenceAccess((path, _) => Task.FromResult<ChangedFile?>(new ChangedFile(path, ChangeType.Edit, "", "a diff")));
        var sut = new AgentAiCommentResolutionCore();

        await sut.EvaluateCodeChangeAsync(
            BuildAnchoredThread(),
            pr,
            chatClient,
            ModelId,
            CancellationToken.None,
            null,
            false,
            evidence);

        await chatClient.Received(1)
            .GetResponseAsync(
                Arg.Is<IList<ChatMessage>>(msgs =>
                    msgs.All(m => m.Text == null || !m.Text.Contains("needFiles"))),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_AsksForEvidenceOnlyOnce()
    {
        // The second round is final. A model that requests again is evaluated on what it already holds, so
        // no thread can traverse the pull request through repeated requests.
        var chatClient = BuildChatClient(
            """{"resolved": false, "replyText": null, "needFiles": ["src/Service.cs"]}""",
            """{"resolved": false, "replyText": null, "needFiles": ["src/Service.cs"]}""");
        var evidence = new ThreadEvidenceAccess((path, _) => Task.FromResult<ChangedFile?>(new ChangedFile(path, ChangeType.Edit, "", "a diff")));
        var sut = new AgentAiCommentResolutionCore();

        var result = await sut.EvaluateCodeChangeAsync(
            BuildAnchoredThread(),
            BuildPrWithManifest(),
            chatClient,
            ModelId,
            CancellationToken.None,
            null,
            false,
            evidence);

        Assert.Equal(2, result.ModelCallCount);
        await chatClient.Received(2)
            .GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_WhenTheRequestedFileCannotBeFetched_KeepsTheFirstVerdict()
    {
        var chatClient = BuildChatClient(
            """{"resolved": false, "replyText": "Still missing.", "needFiles": ["src/Service.cs"]}""",
            """{"resolved": true, "replyText": "unreachable"}""");
        var evidence = new ThreadEvidenceAccess((_, _) => Task.FromResult<ChangedFile?>(null));
        var sut = new AgentAiCommentResolutionCore();

        var result = await sut.EvaluateCodeChangeAsync(
            BuildAnchoredThread(),
            BuildPrWithManifest(),
            chatClient,
            ModelId,
            CancellationToken.None,
            null,
            false,
            evidence);

        Assert.False(result.IsResolved);
        Assert.Equal("Still missing.", result.ReplyText);
        Assert.Equal(1, result.ModelCallCount);
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_WhenTheFetchThrows_KeepsTheFirstVerdict()
    {
        var chatClient = BuildChatClient(
            """{"resolved": false, "replyText": null, "needFiles": ["src/Service.cs"]}""",
            """{"resolved": true, "replyText": "unreachable"}""");
        var evidence = new ThreadEvidenceAccess((_, _) => throw new InvalidOperationException("provider is down"));
        var sut = new AgentAiCommentResolutionCore();

        var result = await sut.EvaluateCodeChangeAsync(
            BuildAnchoredThread(),
            BuildPrWithManifest(),
            chatClient,
            ModelId,
            CancellationToken.None,
            null,
            false,
            evidence);

        Assert.False(result.IsResolved);
        Assert.Equal(1, result.ModelCallCount);
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_RequestedFilesBeyondTheContextBudget_AreLeftOut()
    {
        var chatClient = BuildChatClient(
            """{"resolved": false, "replyText": null, "needFiles": ["src/Service.cs", "src/Other.cs"]}""",
            """{"resolved": true, "replyText": "Validated."}""");
        var evidence = new ThreadEvidenceAccess(
            (path, _) => Task.FromResult<ChangedFile?>(
                new ChangedFile(
                    path,
                    ChangeType.Edit,
                    "",
                    (path.Contains("Service", StringComparison.Ordinal) ? "SERVICEDIFF" : "OTHERDIFF")
                    + new string('x', 40_000))),
            // A window with room for one of these diffs but not the second.
            MaxContextTokens: 14_000);
        var pr = BuildPrWithManifest() with
        {
            AllChangedFileSummaries = new List<ChangedFileSummary>
            {
                new("src/Target.cs", ChangeType.Edit),
                new("src/Service.cs", ChangeType.Edit),
                new("src/Other.cs", ChangeType.Edit),
            }.AsReadOnly(),
        };
        var sut = new AgentAiCommentResolutionCore();

        await sut.EvaluateCodeChangeAsync(
            BuildAnchoredThread(),
            pr,
            chatClient,
            ModelId,
            CancellationToken.None,
            null,
            false,
            evidence);

        await chatClient.Received(1)
            .GetResponseAsync(
                Arg.Is<IList<ChatMessage>>(msgs =>
                    msgs.Any(m => m.Text != null && m.Text.Contains("SERVICEDIFF"))
                    && msgs.All(m => m.Text == null || !m.Text.Contains("OTHERDIFF"))),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_SumsTheTokensOfEveryRoundItSpent()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                BuildResponseWithUsage("""{"resolved": false, "needFiles": ["src/Service.cs"]}""", 100, 10),
                BuildResponseWithUsage("""{"resolved": true, "replyText": "Validated."}""", 250, 30));
        var evidence = new ThreadEvidenceAccess((path, _) => Task.FromResult<ChangedFile?>(new ChangedFile(path, ChangeType.Edit, "", "a diff")));
        var sut = new AgentAiCommentResolutionCore();

        var result = await sut.EvaluateCodeChangeAsync(
            BuildAnchoredThread(),
            BuildPrWithManifest(),
            chatClient,
            ModelId,
            CancellationToken.None,
            null,
            false,
            evidence);

        Assert.Equal(350, result.InputTokens);
        Assert.Equal(40, result.OutputTokens);
        Assert.NotNull(result.Calls);
        Assert.Equal(2, result.Calls!.Count);
        Assert.Equal(100, result.Calls[0].InputTokens);
        Assert.Equal(250, result.Calls[1].InputTokens);
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_WithoutEvidenceAccess_SpendsOneCallAndIgnoresAnyRequest()
    {
        var chatClient = BuildChatClient("""{"resolved": false, "replyText": "Not yet.", "needFiles": ["src/Service.cs"]}""");
        var sut = new AgentAiCommentResolutionCore();

        var result = await sut.EvaluateCodeChangeAsync(BuildAnchoredThread(), BuildPrWithManifest(), chatClient, ModelId);

        Assert.Equal(1, result.ModelCallCount);
        await chatClient.Received(1)
            .GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_ListsEveryChangedFile_WithoutTheirDiffs()
    {
        var chatClient = BuildChatClient("""{"resolved": false, "replyText": null}""");
        var sut = new AgentAiCommentResolutionCore();

        await sut.EvaluateCodeChangeAsync(BuildAnchoredThread(), BuildPrWithManifest(), chatClient, ModelId);

        await chatClient.Received(1)
            .GetResponseAsync(
                Arg.Is<IList<ChatMessage>>(msgs =>
                    msgs.Any(m => m.Text != null && m.Text.Contains("src/Service.cs"))
                    && msgs.Any(m => m.Text != null && m.Text.Contains("diff for target"))),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_TellsTheModelHowToAskAndThenThatItIsTheLastCall()
    {
        var chatClient = BuildChatClient(
            """{"resolved": false, "replyText": null, "needFiles": ["src/Service.cs"]}""",
            """{"resolved": true, "replyText": "Validated."}""");
        var evidence = new ThreadEvidenceAccess((path, _) => Task.FromResult<ChangedFile?>(new ChangedFile(path, ChangeType.Edit, "", "a diff")));
        var sut = new AgentAiCommentResolutionCore();

        await sut.EvaluateCodeChangeAsync(
            BuildAnchoredThread(),
            BuildPrWithManifest(),
            chatClient,
            ModelId,
            CancellationToken.None,
            null,
            false,
            evidence);

        await chatClient.Received(1)
            .GetResponseAsync(
                Arg.Is<IList<ChatMessage>>(msgs =>
                    msgs.Any(m => m.Role == ChatRole.System && m.Text != null && m.Text.Contains("needFiles"))
                    && msgs.All(m => m.Text == null || !m.Text.Contains("final call"))),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());
        await chatClient.Received(1)
            .GetResponseAsync(
                Arg.Is<IList<ChatMessage>>(msgs =>
                    msgs.Any(m => m.Role == ChatRole.System && m.Text != null && m.Text.Contains("final call"))),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_WithoutEvidenceAccess_LeavesTheAskingRulesOut()
    {
        var chatClient = BuildChatClient("""{"resolved": false, "replyText": null}""");
        var sut = new AgentAiCommentResolutionCore();

        await sut.EvaluateCodeChangeAsync(BuildAnchoredThread(), BuildPrWithManifest(), chatClient, ModelId);

        await chatClient.Received(1)
            .GetResponseAsync(
                Arg.Is<IList<ChatMessage>>(msgs =>
                    msgs.All(m => m.Text == null || !m.Text.Contains("needFiles"))),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_RequestedPathOutsideTheManifest_IsReportedToTheCaller()
    {
        // A refusal indicates that something is directing the reviewer at code outside the change, so the
        // caller is notified rather than left to infer it from a thread that never resolves.
        var chatClient = BuildChatClient("""{"resolved": false, "replyText": null, "needFiles": ["src/Secrets.cs"]}""");
        var rejected = new List<string>();
        var evidence = new ThreadEvidenceAccess(
            (path, _) => Task.FromResult<ChangedFile?>(new ChangedFile(path, ChangeType.Edit, "", "a diff")),
            OnRequestRejected: rejected.Add);
        var sut = new AgentAiCommentResolutionCore();

        await sut.EvaluateCodeChangeAsync(
            BuildAnchoredThread(),
            BuildPrWithManifest(),
            chatClient,
            ModelId,
            CancellationToken.None,
            null,
            false,
            evidence);

        Assert.Equal(["src/Secrets.cs"], rejected);
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_AsksForMoreFilesThanAllowed_FetchesOnlyTheAllowedNumber()
    {
        var chatClient = BuildChatClient(
            """{"resolved": false, "replyText": null, "needFiles": ["src/A.cs","src/B.cs","src/C.cs","src/D.cs","src/E.cs","src/F.cs","src/G.cs"]}""",
            """{"resolved": true, "replyText": "Validated."}""");
        var fetched = new List<string>();
        var evidence = new ThreadEvidenceAccess((path, _) =>
        {
            fetched.Add(path);
            return Task.FromResult<ChangedFile?>(new ChangedFile(path, ChangeType.Edit, "", "a diff"));
        });
        var pr = BuildPrWithManifest() with
        {
            AllChangedFileSummaries = new[] { "src/Target.cs", "src/A.cs", "src/B.cs", "src/C.cs", "src/D.cs", "src/E.cs", "src/F.cs", "src/G.cs" }
                .Select(path => new ChangedFileSummary(path, ChangeType.Edit))
                .ToList()
                .AsReadOnly(),
        };
        var sut = new AgentAiCommentResolutionCore();

        await sut.EvaluateCodeChangeAsync(
            BuildAnchoredThread(),
            pr,
            chatClient,
            ModelId,
            CancellationToken.None,
            null,
            false,
            evidence);

        Assert.Equal(5, fetched.Count);
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_ManyChangedFiles_ListsTheOnesNearestTheThreadFirst()
    {
        // The listing is bounded, so what it has room for should be where a fix for this finding would be.
        var chatClient = BuildChatClient("""{"resolved": false, "replyText": null}""");
        var distant = Enumerable.Range(0, 60)
            .Select(index => new ChangedFileSummary($"other/Far{index}.cs", ChangeType.Edit));
        var summaries = distant
            .Append(new ChangedFileSummary("src/Neighbour.cs", ChangeType.Edit))
            .ToList()
            .AsReadOnly();
        var pr = BuildPrWithManifest() with { AllChangedFileSummaries = summaries };
        var sut = new AgentAiCommentResolutionCore();

        await sut.EvaluateCodeChangeAsync(BuildAnchoredThread(), pr, chatClient, ModelId);

        await chatClient.Received(1)
            .GetResponseAsync(
                Arg.Is<IList<ChatMessage>>(msgs =>
                    msgs.Any(m => m.Text != null && m.Text.Contains("src/Neighbour.cs"))),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());
    }

    private static ChatResponse BuildResponseWithUsage(string json, long inputTokens, long outputTokens)
    {
        return new ChatResponse([new ChatMessage(ChatRole.Assistant, json)])
        {
            Usage = new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = outputTokens },
        };
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_WhenAiReturnsResolved_ReturnsIsResolvedTrue()
    {
        var chatClient = BuildChatClient("""{"resolved": true, "replyText": "Fixed in latest commit."}""");
        var sut = new AgentAiCommentResolutionCore();
        var thread = BuildThread("1", ("Bot", "Null reference on line 10.", null));
        var pr = BuildPr();

        var result = await sut.EvaluateCodeChangeAsync(thread, pr, chatClient, ModelId);

        Assert.True(result.IsResolved);
        Assert.Equal("Fixed in latest commit.", result.ReplyText);
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_WhenAiMarksResolvedWithoutReplyText_ReturnsUnresolved()
    {
        var chatClient = BuildChatClient("""{"resolved": true, "replyText": null}""");
        var sut = new AgentAiCommentResolutionCore();
        var thread = BuildThread("1", ("Bot", "Null reference on line 10.", null));
        var pr = BuildPr();

        var result = await sut.EvaluateCodeChangeAsync(thread, pr, chatClient, ModelId);

        Assert.False(result.IsResolved);
        Assert.Null(result.ReplyText);
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_WhenAiMarksResolvedWithWhitespaceReplyText_ReturnsUnresolved()
    {
        var chatClient = BuildChatClient("""{"resolved": true, "replyText": "   "}""");
        var sut = new AgentAiCommentResolutionCore();
        var thread = BuildThread("1", ("Bot", "Null reference on line 10.", null));
        var pr = BuildPr();

        var result = await sut.EvaluateCodeChangeAsync(thread, pr, chatClient, ModelId);

        Assert.False(result.IsResolved);
        Assert.Null(result.ReplyText);
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_WhenAiReturnsUnresolved_ReturnsIsResolvedFalse()
    {
        var chatClient = BuildChatClient("""{"resolved": false, "replyText": null}""");
        var sut = new AgentAiCommentResolutionCore();
        var thread = BuildThread("1", ("Bot", "Potential race condition.", null));
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
        var thread = BuildThread("1", ("Bot", "Consider edge case.", null));
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
            "1",
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
            "1",
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
            "1",
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
        var thread = BuildThread("1", ("Bot", "Missing null check on line 10.", null));
        var pr = BuildPr();

        await sut.EvaluateCodeChangeAsync(thread, pr, chatClient, ModelId);

        await chatClient.Received(1)
            .GetResponseAsync(
                Arg.Is<IList<ChatMessage>>(msgs =>
                    msgs.Any(m => m.Text != null && m.Text.Contains("Missing null check"))),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateConversationalReplyAsync_SendsThreadHistory_ToChatClient()
    {
        var chatClient = BuildChatClient("""{"resolved": false, "replyText": "Because of X."}""");
        var sut = new AgentAiCommentResolutionCore();
        var thread = BuildThread(
            "1",
            ("Bot", "Use StringBuilder here.", null),
            ("Dev", "Why StringBuilder?", null));

        await sut.EvaluateConversationalReplyAsync(thread, chatClient, ModelId);

        await chatClient.Received(1)
            .GetResponseAsync(
                Arg.Is<IList<ChatMessage>>(msgs =>
                    msgs.Any(m => m.Text != null && m.Text.Contains("Why StringBuilder"))),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_OnlyIncludesMatchingFileDiff_NotOtherFiles()
    {
        // Arrange: PR with two changed files; thread anchored to only one of them.
        var chatClient = BuildChatClient("""{"resolved": true, "replyText": null}""");
        var sut = new AgentAiCommentResolutionCore();

        var comments = new List<PrThreadComment> { new("Bot", "Null check missing.") }.AsReadOnly();
        var thread = new PrCommentThread("1", "/src/Target.cs", 5, comments);

        var targetFile = new ChangedFile("/src/Target.cs", ChangeType.Edit, "", "diff for target");
        var otherFile = new ChangedFile("/src/Other.cs", ChangeType.Edit, "", "diff for other");
        var pr = new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            1,
            2,
            "Fix",
            null,
            "feature/fix",
            "main",
            new List<ChangedFile> { targetFile, otherFile }.AsReadOnly());

        await sut.EvaluateCodeChangeAsync(thread, pr, chatClient, ModelId);

        await chatClient.Received(1)
            .GetResponseAsync(
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

        var comments = new List<PrThreadComment> { new("Bot", "Issue here.") }.AsReadOnly();
        var thread = new PrCommentThread("1", "/src/Missing.cs", 1, comments);

        var otherFile = new ChangedFile("/src/Other.cs", ChangeType.Edit, "", "diff for other");
        var pr = new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            1,
            2,
            "Fix",
            null,
            "feature/fix",
            "main",
            new List<ChangedFile> { otherFile }.AsReadOnly());

        await sut.EvaluateCodeChangeAsync(thread, pr, chatClient, ModelId);

        await chatClient.Received(1)
            .GetResponseAsync(
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
        var comments = new List<PrThreadComment> { new("Bot", "Overall design concern.") }.AsReadOnly();
        var thread = new PrCommentThread("1", null, null, comments);

        var fileA = new ChangedFile("/src/A.cs", ChangeType.Edit, "", "big diff A");
        var fileB = new ChangedFile("/src/B.cs", ChangeType.Add, "", "big diff B");
        var pr = new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            1,
            2,
            "Fix",
            null,
            "feature/fix",
            "main",
            new List<ChangedFile> { fileA, fileB }.AsReadOnly());

        await sut.EvaluateCodeChangeAsync(thread, pr, chatClient, ModelId);

        await chatClient.Received(1)
            .GetResponseAsync(
                Arg.Is<IList<ChatMessage>>(msgs =>
                    msgs.Any(m => m.Text != null && m.Text.Contains("/src/A.cs")) &&
                    msgs.Any(m => m.Text != null && m.Text.Contains("/src/B.cs")) &&
                    msgs.All(m => m.Text == null || !m.Text.Contains("big diff A")) &&
                    msgs.All(m => m.Text == null || !m.Text.Contains("big diff B"))),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_WhenTheDeveloperAlsoReplied_ShowsTheirWordsAndAsksForAnAnswer()
    {
        var chatClient = BuildChatClient("""{"resolved": false, "replyText": "The cast is the part I meant."}""");
        var sut = new AgentAiCommentResolutionCore();
        var thread = BuildThread(
            "1",
            ("Bot", "Missing null check on line 10.", null),
            ("Dev", "Fixed, though I kept the null check because the caller can pass null.", null));

        await sut.EvaluateCodeChangeAsync(thread, BuildPr(), chatClient, ModelId, CancellationToken.None, null, true);

        await chatClient.Received(1)
            .GetResponseAsync(
                Arg.Is<IList<ChatMessage>>(msgs =>
                    msgs.Any(m => m.Role == ChatRole.System
                                  && m.Text != null
                                  && m.Text.Contains("answer the person as well as judging the finding"))
                    && msgs.Any(m => m.Role == ChatRole.User
                                     && m.Text != null
                                     && m.Text.Contains("because the caller can pass null"))),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_WhenNobodyReplied_LeavesTheAnswerThePersonRuleOut()
    {
        var chatClient = BuildChatClient("""{"resolved": false, "replyText": null}""");
        var sut = new AgentAiCommentResolutionCore();
        var thread = BuildThread("1", ("Bot", "Missing null check on line 10.", null));

        await sut.EvaluateCodeChangeAsync(thread, BuildPr(), chatClient, ModelId);

        await chatClient.Received(1)
            .GetResponseAsync(
                Arg.Is<IList<ChatMessage>>(msgs =>
                    msgs.All(m => m.Text == null
                                  || !m.Text.Contains("answer the person as well as judging the finding"))),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateCodeChangeAsync_WithConfiguredOutputLanguage_StatesItInTheSystemPrompt()
    {
        var chatClient = BuildChatClient("""{"resolved": false, "replyText": null}""");
        var sut = new AgentAiCommentResolutionCore();
        var thread = BuildThread("1", ("Bot", "Null reference on line 10.", null));

        await sut.EvaluateCodeChangeAsync(thread, BuildPr(), chatClient, ModelId, CancellationToken.None, "de");

        await chatClient.Received(1)
            .GetResponseAsync(
                Arg.Is<IList<ChatMessage>>(msgs =>
                    msgs.Any(m => m.Role == ChatRole.System && m.Text != null && m.Text.Contains("`de`"))),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateConversationalReplyAsync_WithConfiguredOutputLanguage_StatesItInTheSystemPrompt()
    {
        var chatClient = BuildChatClient("""{"resolved": false, "replyText": "Still open."}""");
        var sut = new AgentAiCommentResolutionCore();
        var thread = BuildThread("1", ("Dev", "Why is this a problem?", null));

        await sut.EvaluateConversationalReplyAsync(thread, chatClient, ModelId, CancellationToken.None, "de");

        await chatClient.Received(1)
            .GetResponseAsync(
                Arg.Is<IList<ChatMessage>>(msgs =>
                    msgs.Any(m => m.Role == ChatRole.System && m.Text != null && m.Text.Contains("`de`"))),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());
    }
}

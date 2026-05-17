// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.ProRV.Abstractions;
using MeisterProPR.ProRV.Core;
using MeisterProPR.ProRV.Knowledge;
using MeisterProPR.ProRV.Models;
using MeisterProPR.ProRV.Prompting;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Knowledge.ProRV;

public sealed class ProRVPrefilterTests
{
    private readonly IChatClient chatClient = Substitute.For<IChatClient>();

    private readonly IProRVPrefilter sut = new ProRVPrefilter(
        new EmbeddedProRVKnowledgeCatalog(),
        new ProRVPromptFactory(new EmbeddedProRVKnowledgeCatalog()));

    [Fact]
    public async Task RankRelevantItemsAsync_ReturnsRankedItemsForResolvedLanguage()
    {
        this.chatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        """
                        {
                          "ranked_checks": [
                            { "id": "cs/web/xss", "score": 96, "reason": "Writes request input directly to a response path." },
                            { "id": "cs/xml-injection", "score": 41, "reason": "Touches request-derived content in a response context." }
                          ]
                        }
                        """)));

        var result = await this.sut.RankRelevantItemsAsync(
            new ProRVPrefilterRequest(
                "src/Web/ErrorPage.cs",
                "+ var page = Request.QueryString[\"page\"];\n+ Response.Write(page);")
            {
                TechnologyHints = ["aspnet", "dotnet"],
                MaxResults = 5,
            },
            this.chatClient,
            new ChatOptions { ModelId = "prefilter-model" });

        Assert.Equal(ProRVPrefilterStatus.Success, result.Status);
        Assert.Equal("csharp", result.Language);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal("cs/web/xss", result.Items[0].Id);
        Assert.Equal(96, result.Items[0].Score);
        Assert.Contains("What to look for:", result.Items[0].Instruction, StringComparison.Ordinal);
        Assert.NotNull(result.RawResponse);
    }

    [Fact]
    public async Task RankRelevantItemsAsync_UnsupportedLanguage_ReturnsUnsupportedStatusWithoutCallingChatClient()
    {
        var result = await this.sut.RankRelevantItemsAsync(
            new ProRVPrefilterRequest("src/ui/component.vue", "+ <template>Hello</template>"),
            this.chatClient);

        Assert.Equal(ProRVPrefilterStatus.UnsupportedLanguage, result.Status);
        Assert.Empty(result.Items);
        await this.chatClient.DidNotReceiveWithAnyArgs()
            .GetResponseAsync(default!);
    }

    [Fact]
    public async Task RankRelevantItemsAsync_TypescriptFile_UsesJavascriptKnowledgeBundle()
    {
        this.chatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        """
                        {
                          "ranked_checks": [
                            { "id": "js/incomplete-sanitization", "score": 88, "reason": "Build content pipes data into a DOM/render path with incomplete validation." }
                          ]
                        }
                        """)));

        var result = await this.sut.RankRelevantItemsAsync(
            new ProRVPrefilterRequest(
                "scripts/build-content.ts",
                "+export function render(content: string) { return `<div>${content}</div>`; }")
            {
                TechnologyHints = ["typescript", "node"],
            },
            this.chatClient,
            new ChatOptions { ModelId = "prefilter-model" });

        Assert.Equal(ProRVPrefilterStatus.Success, result.Status);
        Assert.Equal("javascript", result.Language);
        var check = Assert.Single(result.Items);
        Assert.Equal("js/incomplete-sanitization", check.Id);
        Assert.Contains("What to look for:", check.Instruction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RankRelevantItemsAsync_ActionsWorkflow_UsesActionsKnowledgeBundle()
    {
        this.chatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        """
                        {
                          "ranked_checks": [
                            { "id": "actions/missing-workflow-permissions", "score": 91, "reason": "Adds a workflow job without an explicit permissions block." }
                          ]
                        }
                        """)));

        var result = await this.sut.RankRelevantItemsAsync(
            new ProRVPrefilterRequest(
                ".github/workflows/build.yml",
                "+jobs:\n+  build:\n+    runs-on: ubuntu-latest\n+    steps:\n+      - uses: actions/checkout@v4")
            {
                Language = "actions",
            },
            this.chatClient,
            new ChatOptions { ModelId = "prefilter-model" });

        Assert.Equal(ProRVPrefilterStatus.Success, result.Status);
        Assert.Equal("actions", result.Language);
        var check = Assert.Single(result.Items);
        Assert.Equal("actions/missing-workflow-permissions", check.Id);
        Assert.Contains("What to look for:", check.Instruction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RankRelevantItemsAsync_ActionsWorkflow_LoadsInstructionFromHyphenatedDirectory()
    {
        this.chatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        """
                        {
                          "ranked_checks": [
                            { "id": "actions/envvar-injection/critical", "score": 97, "reason": "Writes a workflow environment variable from untrusted input." }
                          ]
                        }
                        """)));

        var result = await this.sut.RankRelevantItemsAsync(
            new ProRVPrefilterRequest(
                ".github/workflows/publish.yml",
                "+      - run: echo \"DEPLOY_ENV=${{ github.event.pull_request.title }}\" >> $GITHUB_ENV")
            {
                Language = "actions",
            },
            this.chatClient,
            new ChatOptions { ModelId = "prefilter-model" });

        Assert.Equal(ProRVPrefilterStatus.Success, result.Status);
        var check = Assert.Single(result.Items);
        Assert.Equal("actions/envvar-injection/critical", check.Id);
        Assert.Contains("What to look for:", check.Instruction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RankRelevantItemsAsync_ActionsWorkflow_InfersActionsLanguageFromFilePath()
    {
        this.chatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        """
                        {
                          "ranked_checks": [
                            { "id": "actions/missing-workflow-permissions", "score": 91, "reason": "Adds a workflow job without an explicit permissions block." }
                          ]
                        }
                        """)));

        var result = await this.sut.RankRelevantItemsAsync(
            new ProRVPrefilterRequest(
                ".github/workflows/build.yml",
                "+jobs:\n+  build:\n+    runs-on: ubuntu-latest\n+    steps:\n+      - uses: actions/checkout@v4"),
            this.chatClient,
            new ChatOptions { ModelId = "prefilter-model" });

        Assert.Equal(ProRVPrefilterStatus.Success, result.Status);
        Assert.Equal("actions", result.Language);
        Assert.Equal("actions/missing-workflow-permissions", Assert.Single(result.Items).Id);
    }

    [Theory]
    [InlineData("src/main.py", "python", "py/missing-call-to-init")]
    [InlineData("src/main.go", "go", "go/constant-length-comparison")]
    [InlineData("src/Main.java", "java", "java/missing-override-annotation")]
    [InlineData("src/lib.rs", "rust", "rust/regex-injection")]
    [InlineData("src/app.rb", "ruby", "rb/incomplete-hostname-regexp")]
    [InlineData("src/App.swift", "swift", "swift/incomplete-hostname-regexp")]
    [InlineData("src/main.cpp", "cpp", "cpp/feature-envy")]
    public async Task RankRelevantItemsAsync_NewSourceLanguage_UsesEmbeddedKnowledgeBundle(string filePath, string expectedLanguage, string checkId)
    {
        this.chatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        $"{{\"ranked_checks\":[{{\"id\":\"{checkId}\",\"score\":87,\"reason\":\"Diff matches a representative {expectedLanguage} review pattern.\"}}]}}")));

        var result = await this.sut.RankRelevantItemsAsync(
            new ProRVPrefilterRequest(filePath, "+ changed source line"),
            this.chatClient,
            new ChatOptions { ModelId = "prefilter-model" });

        Assert.Equal(ProRVPrefilterStatus.Success, result.Status);
        Assert.Equal(expectedLanguage, result.Language);
        Assert.Equal(checkId, Assert.Single(result.Items).Id);
        Assert.Contains("What to look for:", result.Items[0].Instruction, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fastapi", "python")]
    [InlineData("golang", "go")]
    [InlineData("maven", "java")]
    [InlineData("rails", "ruby")]
    [InlineData("cargo", "rust")]
    [InlineData("xcode", "swift")]
    [InlineData("clang", "cpp")]
    public async Task RankRelevantItemsAsync_TechnologyHintOnly_ResolvesNewSupportedLanguage(string hint, string expectedLanguage)
    {
        this.chatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        """
                        {
                          "ranked_checks": []
                        }
                        """)));

        var result = await this.sut.RankRelevantItemsAsync(
            new ProRVPrefilterRequest("unknown/file.txt", "+ changed source line")
            {
                TechnologyHints = [hint],
            },
            this.chatClient,
            new ChatOptions { ModelId = "prefilter-model" });

        Assert.Equal(ProRVPrefilterStatus.Success, result.Status);
        Assert.Equal(expectedLanguage, result.Language);
    }

    [Fact]
    public async Task RankRelevantItemsAsync_UnparseableResponse_ReturnsFailureResult()
    {
        this.chatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "not json")));

        var result = await this.sut.RankRelevantItemsAsync(
            new ProRVPrefilterRequest("src/Foo.cs", "+ GC.Collect();"),
            this.chatClient);

        Assert.Equal(ProRVPrefilterStatus.UnparseableResponse, result.Status);
        Assert.Empty(result.Items);
        Assert.Equal("not json", result.RawResponse);
    }
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text;
using System.Text.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.Reviewing;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.Security;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.GitHub;

public sealed class GitHubCodeReviewQueryServiceTests
{
    [Fact]
    public async Task GetReviewAsync_ReturnsNormalizedReviewMetadataAndRevision()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);

        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        connectionRepository.GetOperationalConnectionAsync(clientId, host, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    Guid.NewGuid(),
                    clientId,
                    ScmProvider.GitHub,
                    host.HostBaseUrl,
                    ScmAuthenticationKind.PersonalAccessToken,
                    "GitHub",
                    "ghp_test",
                    true));

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("GitHubProvider")
            .Returns(
                new HttpClient(
                    new StubHttpMessageHandler(request => request.RequestUri!.AbsoluteUri switch
                    {
                        "https://api.github.com/user" => CreateJsonResponse(new { login = "meister-dev" }),
                        "https://api.github.com/repos/acme/propr/pulls/42" => CreateJsonResponse(
                            new
                            {
                                title = "Add provider-neutral adapters",
                                html_url = "https://github.com/acme/propr/pull/42",
                                state = "open",
                                merged_at = (string?)null,
                                head = new { @ref = "feature/providers", sha = "head-sha" },
                                @base = new { @ref = "main", sha = "base-sha" },
                                requested_reviewers = new object[]
                                {
                                    new
                                    {
                                        id = 99, login = "meister-review-bot[bot]", name = "Meister Review Bot",
                                        type = "Bot",
                                    },
                                },
                            }),
                        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                    })));

        var sut = new GitHubCodeReviewQueryService(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var result = await sut.GetReviewAsync(clientId, review);

        Assert.NotNull(result);
        Assert.Equal(CodeReviewState.Open, result!.ReviewState);
        Assert.Equal("Add provider-neutral adapters", result.Title);
        Assert.Equal("feature/providers", result.SourceBranch);
        Assert.Equal("main", result.TargetBranch);
        Assert.Equal("head-sha", result.ReviewRevision!.HeadSha);
        Assert.Equal("base-sha", result.ReviewRevision.BaseSha);
        Assert.Equal("meister-review-bot[bot]", result.RequestedReviewerIdentity!.Login);
    }

    [Fact]
    public async Task PullRequestFetcher_ReturnsChangedFilesAndThreads()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var connectionRepository = CreateConnectionRepository(clientId, host);
        var httpClientFactory = CreateHttpClientFactory(request => request.RequestUri!.AbsoluteUri switch
        {
            "https://api.github.com/user" => CreateJsonResponse(new { login = "meister-dev" }),
            "https://api.github.com/repositories/101" => CreateJsonResponse(new { full_name = "acme/propr" }),
            "https://api.github.com/repos/acme/propr/pulls/42" => CreateJsonResponse(
                new
                {
                    title = "Add provider-neutral fetchers",
                    body = "Fetch GitHub pull requests without Azure-only code.",
                    state = "open",
                    merged_at = (string?)null,
                    head = new { @ref = "feature/providers", sha = "head-sha" },
                    @base = new { @ref = "main", sha = "base-sha" },
                }),
            "https://api.github.com/repos/acme/propr/pulls/42/files?per_page=100" => CreateJsonResponse(
                new object[]
                {
                    new
                    {
                        filename = "src/OldProvider.cs", status = "renamed",
                        previous_filename = "src/LegacyProvider.cs", patch = "rename diff",
                    },
                    new
                    {
                        filename = "src/Fetcher.cs", status = "added", previous_filename = (string?)null,
                        patch = "+class Fetcher",
                    },
                }),
            "https://api.github.com/repos/acme/propr/contents/src%2FOldProvider.cs?ref=head-sha" =>
                CreateContentResponse("public class OldProvider {}"),
            "https://api.github.com/repos/acme/propr/contents/src%2FLegacyProvider.cs?ref=base-sha" =>
                CreateContentResponse("public class LegacyProvider {}"),
            "https://api.github.com/repos/acme/propr/contents/src%2FFetcher.cs?ref=head-sha" => CreateContentResponse("public class Fetcher {}"),
            "https://api.github.com/graphql" => CreateJsonResponse(
                new
                {
                    data = new
                    {
                        repository = new
                        {
                            pullRequest = new
                            {
                                reviewThreads = new
                                {
                                    nodes = new object[]
                                    {
                                        new
                                        {
                                            isResolved = false,
                                            path = "src/Fetcher.cs",
                                            line = 18,
                                            comments = new
                                            {
                                                nodes = new object[]
                                                {
                                                    new
                                                    {
                                                        databaseId = 501,
                                                        body = "Please handle null.",
                                                        createdAt = "2026-04-17T10:00:00Z",
                                                        author = new
                                                            { login = "meister-review-bot[bot]", databaseId = 99 },
                                                    },
                                                },
                                            },
                                        },
                                    },
                                },
                            },
                        },
                    },
                }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var sut = new GitHubPullRequestFetcher(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var result = await sut.FetchAsync(
            "https://github.com",
            "acme",
            "101",
            42,
            7,
            clientId: clientId,
            cancellationToken: CancellationToken.None);

        Assert.Equal("Add provider-neutral fetchers", result.Title);
        Assert.Equal("feature/providers", result.SourceBranch);
        Assert.Equal(2, result.ChangedFiles.Count);
        Assert.Equal(ChangeType.Rename, result.ChangedFiles[0].ChangeType);
        Assert.Equal("src/LegacyProvider.cs", result.ChangedFiles[0].OriginalPath);
        Assert.Equal("src/Fetcher.cs", result.ChangedFiles[1].Path);
        var thread = Assert.Single(result.ExistingThreads!);
        Assert.Equal(501, thread.ThreadId);
        Assert.Equal("src/Fetcher.cs", thread.FilePath);
    }

    [Fact]
    public async Task ReviewContextTools_ReturnChangedFilesTreeContentAndProCursorContext()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var connectionRepository = CreateConnectionRepository(clientId, host);
        var gateway = Substitute.For<IProCursorGateway>();
        gateway.AskKnowledgeAsync(Arg.Any<ProCursorKnowledgeQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ProCursorKnowledgeAnswerDto("ok", []));
        var httpClientFactory = CreateHttpClientFactory(request => request.RequestUri!.AbsoluteUri switch
        {
            "https://api.github.com/user" => CreateJsonResponse(new { login = "meister-dev" }),
            "https://api.github.com/repos/acme/propr/pulls/42/files?per_page=100" => CreateJsonResponse(
                new object[]
                {
                    new { filename = "src/Fetcher.cs", status = "added" },
                    new { filename = "src/NewProvider.cs", status = "renamed" },
                }),
            "https://api.github.com/repos/acme/propr/branches/feature%2Fproviders" => CreateJsonResponse(new { commit = new { sha = "head-sha" } }),
            "https://api.github.com/repos/acme/propr/git/trees/head-sha?recursive=1" => CreateJsonResponse(
                new
                {
                    tree = new object[]
                    {
                        new { path = "src/Fetcher.cs", type = "blob" },
                        new { path = "docs/notes.md", type = "blob" },
                        new { path = "src", type = "tree" },
                    },
                }),
            "https://api.github.com/repos/acme/propr/contents/src%2FFetcher.cs?ref=feature%2Fproviders" =>
                CreateContentResponse("line1\nline2\nline3\nline4"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var factory = new GitHubReviewContextToolsFactory(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory,
            gateway,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileSizeBytes = 1024 * 1024 }),
            NullLogger<GitHubReviewContextTools>.Instance);

        var tools = factory.Create(new ReviewContextToolsRequest(review, "feature/providers", 7, clientId, null, host.HostBaseUrl));

        var changedFiles = await tools.GetChangedFilesAsync(CancellationToken.None);
        var tree = await tools.GetFileTreeAsync("main", CancellationToken.None);
        var content = await tools.GetFileContentAsync("src/Fetcher.cs", "main", 2, 3, CancellationToken.None);
        await tools.AskProCursorKnowledgeAsync("where is the fetcher?", CancellationToken.None);

        Assert.Collection(
            changedFiles,
            item =>
            {
                Assert.Equal("src/Fetcher.cs", item.Path);
                Assert.Equal(ChangeType.Add, item.ChangeType);
            },
            item =>
            {
                Assert.Equal("src/NewProvider.cs", item.Path);
                Assert.Equal(ChangeType.Rename, item.ChangeType);
            });
        Assert.Equal(["src/Fetcher.cs", "docs/notes.md"], tree);
        Assert.Equal("line2\nline3", content);
        await gateway.Received(1)
            .AskKnowledgeAsync(
                Arg.Is<ProCursorKnowledgeQueryRequest>(request =>
                    request.RepositoryContext!.ProviderScopePath == host.HostBaseUrl
                    && request.RepositoryContext.ProviderProjectKey == "acme"
                    && request.RepositoryContext.RepositoryId == "101"
                    && request.RepositoryContext.Branch == "feature/providers"),
                Arg.Any<CancellationToken>());
    }

    private static IClientScmConnectionRepository CreateConnectionRepository(Guid clientId, ProviderHostRef host)
    {
        var repository = Substitute.For<IClientScmConnectionRepository>();
        repository.GetOperationalConnectionAsync(clientId, host, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    Guid.NewGuid(),
                    clientId,
                    ScmProvider.GitHub,
                    host.HostBaseUrl,
                    ScmAuthenticationKind.PersonalAccessToken,
                    "GitHub",
                    "ghp_test",
                    true));
        return repository;
    }

    private static IHttpClientFactory CreateHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("GitHubProvider")
            .Returns(new HttpClient(new StubHttpMessageHandler(request => responder(request))));
        return factory;
    }

    private static HttpResponseMessage CreateContentResponse(string content)
    {
        return CreateJsonResponse(
            new
            {
                content = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)),
                encoding = "base64",
            });
    }

    private static HttpResponseMessage CreateJsonResponse<T>(T payload)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload)),
        };
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(responder(request));
        }
    }
}

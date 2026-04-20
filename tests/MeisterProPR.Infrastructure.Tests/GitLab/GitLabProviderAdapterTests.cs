// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.GitLab.Discovery;
using MeisterProPR.Infrastructure.Features.Providers.GitLab.Identity;
using MeisterProPR.Infrastructure.Features.Providers.GitLab.Reviewing;
using MeisterProPR.Infrastructure.Features.Providers.GitLab.Security;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.GitLab;

public sealed class GitLabConnectionVerifierTests
{
    [Fact]
    public async Task VerifyAsync_ValidPersonalAccessToken_ReturnsAuthenticatedUsername()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var repository = GitLabTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = GitLabTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://gitlab.example.com/api/v4/user" => GitLabTestHelpers.CreateJsonResponse(new { username = "meister-dev" }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var sut = new GitLabConnectionVerifier(repository, httpClientFactory);

        var result = await sut.VerifyAsync(clientId, host);

        Assert.Equal("meister-dev", result.AuthenticatedUsername);
        Assert.Equal("glpat-test", result.Connection.Secret);
    }
}

public sealed class GitLabDiscoveryServiceTests
{
    [Fact]
    public async Task ListScopesAsync_IncludesAuthenticatedUserAndGroups()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var connectionRepository = GitLabTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = GitLabTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://gitlab.example.com/api/v4/user" => GitLabTestHelpers.CreateJsonResponse(new { username = "meister-dev" }),
                "https://gitlab.example.com/api/v4/groups?per_page=100&min_access_level=10" => GitLabTestHelpers
                    .CreateJsonResponse(
                        new[]
                        {
                            new { path = "acme", full_path = "acme" },
                            new { path = "platform", full_path = "acme/platform" },
                        }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var sut = new GitLabDiscoveryService(
            new GitLabConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var scopes = await sut.ListScopesAsync(clientId, host);

        Assert.Equal(["acme", "acme/platform", "meister-dev"], scopes);
    }

    [Fact]
    public async Task ListRepositoriesAsync_GroupScope_ReturnsNormalizedRepositoryReferences()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var connectionRepository = GitLabTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = GitLabTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://gitlab.example.com/api/v4/user" => GitLabTestHelpers.CreateJsonResponse(new { username = "meister-dev" }),
                "https://gitlab.example.com/api/v4/groups/acme%2Fplatform/projects?include_subgroups=true&simple=true&per_page=100"
                    => GitLabTestHelpers.CreateJsonResponse(
                        new[]
                        {
                            new
                            {
                                id = 101, path_with_namespace = "acme/platform/propr",
                                @namespace = new { full_path = "acme/platform" },
                            },
                            new
                            {
                                id = 102, path_with_namespace = "acme/platform/another",
                                @namespace = new { full_path = "acme/platform" },
                            },
                        }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var sut = new GitLabDiscoveryService(
            new GitLabConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var repositories = await sut.ListRepositoriesAsync(clientId, host, "acme/platform");

        Assert.Equal(2, repositories.Count);
        Assert.Equal("101", repositories[0].ExternalRepositoryId);
        Assert.Equal("acme/platform", repositories[0].OwnerOrNamespace);
        Assert.Equal("acme/platform/propr", repositories[0].ProjectPath);
    }
}

public sealed class GitLabReviewerIdentityServiceTests
{
    [Fact]
    public async Task ResolveCandidatesAsync_ReturnsSortedReviewerIdentities()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var connectionRepository = GitLabTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = GitLabTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://gitlab.example.com/api/v4/user" => GitLabTestHelpers.CreateJsonResponse(new { username = "meister-dev" }),
                "https://gitlab.example.com/api/v4/users?search=meister&per_page=20" => GitLabTestHelpers
                    .CreateJsonResponse(
                        new object[]
                        {
                            new { id = 2, username = "meister-review-bot", name = "Meister Review Bot", bot = true },
                            new { id = 1, username = "meister-dev", name = "Meister Dev", bot = false },
                        }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var sut = new GitLabReviewerIdentityService(
            new GitLabConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var candidates = await sut.ResolveCandidatesAsync(clientId, host, "meister");

        Assert.Equal(2, candidates.Count);
        Assert.Equal("meister-dev", candidates[0].Login);
        Assert.False(candidates[0].IsBot);
        Assert.True(candidates[1].IsBot);
    }
}

public sealed class GitLabCodeReviewQueryServiceTests
{
    [Fact]
    public async Task GetReviewAsync_ReturnsNormalizedReviewMetadataAndRevision()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var repository = new RepositoryRef(host, "101", "acme/platform", "acme/platform/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "4201", 42);
        var connectionRepository = GitLabTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = GitLabTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://gitlab.example.com/api/v4/user" => GitLabTestHelpers.CreateJsonResponse(new { username = "meister-dev" }),
                "https://gitlab.example.com/api/v4/projects/101/merge_requests/42" => GitLabTestHelpers
                    .CreateJsonResponse(
                        new
                        {
                            id = 4201,
                            iid = 42,
                            title = "Add provider-neutral adapters",
                            web_url = "https://gitlab.example.com/acme/platform/propr/-/merge_requests/42",
                            state = "opened",
                            draft = false,
                            source_branch = "feature/providers",
                            target_branch = "main",
                            sha = "head-sha",
                            diff_refs = new { base_sha = "base-sha", head_sha = "head-sha", start_sha = "start-sha" },
                            reviewers = new object[]
                            {
                                new
                                {
                                    id = 99, username = "meister-review-bot", name = "Meister Review Bot", bot = true,
                                },
                            },
                        }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var sut = new GitLabCodeReviewQueryService(
            new GitLabConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var result = await sut.GetReviewAsync(clientId, review);

        Assert.NotNull(result);
        Assert.Equal(CodeReviewState.Open, result!.ReviewState);
        Assert.Equal("Add provider-neutral adapters", result.Title);
        Assert.Equal("feature/providers", result.SourceBranch);
        Assert.Equal("main", result.TargetBranch);
        Assert.Equal("head-sha", result.ReviewRevision!.HeadSha);
        Assert.Equal("base-sha", result.ReviewRevision.BaseSha);
        Assert.Equal("meister-review-bot", result.RequestedReviewerIdentity!.Login);
    }
}

public sealed class GitLabPullRequestFetcherTests
{
    [Fact]
    public async Task FetchAsync_ReturnsChangedFilesAndThreads()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var connectionRepository = GitLabTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = GitLabTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://gitlab.example.com/api/v4/user" => GitLabTestHelpers.CreateJsonResponse(new { username = "meister-dev" }),
                "https://gitlab.example.com/api/v4/projects/101/merge_requests/42" => GitLabTestHelpers
                    .CreateJsonResponse(
                        new
                        {
                            title = "Add provider fetchers",
                            description = "Fetch PRs without Azure-specific infrastructure.",
                            state = "opened",
                            source_branch = "feature/providers",
                            target_branch = "main",
                            sha = "head-sha",
                            diff_refs = new { base_sha = "base-sha", head_sha = "head-sha", start_sha = "start-sha" },
                            references = new { full = "acme/platform/propr!42", @short = "propr!42" },
                        }),
                "https://gitlab.example.com/api/v4/projects/101/merge_requests/42/changes" => GitLabTestHelpers
                    .CreateJsonResponse(
                        new
                        {
                            changes = new object[]
                            {
                                new
                                {
                                    old_path = "src/OldProvider.cs", new_path = "src/NewProvider.cs",
                                    diff = "rename diff", new_file = false, deleted_file = false, renamed_file = true,
                                },
                                new
                                {
                                    old_path = (string?)null, new_path = "src/Fetcher.cs", diff = "+class Fetcher",
                                    new_file = true, deleted_file = false, renamed_file = false,
                                },
                            },
                        }),
                "https://gitlab.example.com/api/v4/projects/101/repository/files/src%2FNewProvider.cs/raw?ref=head-sha"
                    => new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("public class NewProvider {}") },
                "https://gitlab.example.com/api/v4/projects/101/repository/files/src%2FOldProvider.cs/raw?ref=base-sha"
                    => new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("public class OldProvider {}") },
                "https://gitlab.example.com/api/v4/projects/101/repository/files/src%2FFetcher.cs/raw?ref=head-sha" =>
                    new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("public class Fetcher {}") },
                "https://gitlab.example.com/api/v4/projects/101/merge_requests/42/discussions?per_page=100" =>
                    GitLabTestHelpers.CreateJsonResponse(
                        new object[]
                        {
                            new
                            {
                                individual_note = false,
                                notes = new object[]
                                {
                                    new
                                    {
                                        id = 501,
                                        body = "Please handle null.",
                                        system = false,
                                        resolved = false,
                                        created_at = "2026-04-17T10:00:00Z",
                                        author = new { id = 99, username = "meister-review-bot" },
                                        position = new
                                        {
                                            new_path = "src/Fetcher.cs", old_path = "src/Fetcher.cs", new_line = 18,
                                            old_line = (int?)null,
                                        },
                                    },
                                },
                            },
                        }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var sut = new GitLabPullRequestFetcher(
            new GitLabConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var result = await sut.FetchAsync(
            "https://gitlab.example.com",
            "acme/platform",
            "101",
            42,
            7,
            clientId: clientId,
            cancellationToken: CancellationToken.None);

        Assert.Equal("Add provider fetchers", result.Title);
        Assert.Equal("feature/providers", result.SourceBranch);
        Assert.Equal(2, result.ChangedFiles.Count);
        Assert.Equal(ChangeType.Rename, result.ChangedFiles[0].ChangeType);
        Assert.Equal("src/OldProvider.cs", result.ChangedFiles[0].OriginalPath);
        Assert.Equal("src/Fetcher.cs", result.ChangedFiles[1].Path);
        var thread = Assert.Single(result.ExistingThreads!);
        Assert.Equal(501, thread.ThreadId);
        Assert.Equal("src/Fetcher.cs", thread.FilePath);
    }
}

public sealed class GitLabReviewContextToolsTests
{
    [Fact]
    public async Task FactoryCreatedTools_ReturnChangedFilesTreeContentAndProCursorContext()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var repository = new RepositoryRef(host, "101", "acme/platform", "acme/platform/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "4201", 42);
        var connectionRepository = GitLabTestHelpers.CreateConnectionRepository(clientId, host);
        var gateway = Substitute.For<IProCursorGateway>();
        gateway.AskKnowledgeAsync(Arg.Any<ProCursorKnowledgeQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ProCursorKnowledgeAnswerDto("ok", []));
        var httpClientFactory = GitLabTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://gitlab.example.com/api/v4/user" => GitLabTestHelpers.CreateJsonResponse(new { username = "meister-dev" }),
                "https://gitlab.example.com/api/v4/projects/101/merge_requests/42/changes" => GitLabTestHelpers
                    .CreateJsonResponse(
                        new
                        {
                            changes = new object[]
                            {
                                new
                                {
                                    old_path = "src/OldProvider.cs", new_path = "src/NewProvider.cs", new_file = false,
                                    deleted_file = false, renamed_file = true,
                                },
                                new
                                {
                                    old_path = (string?)null, new_path = "src/Fetcher.cs", new_file = true,
                                    deleted_file = false, renamed_file = false,
                                },
                            },
                        }),
                "https://gitlab.example.com/api/v4/projects/101/repository/tree?recursive=true&per_page=100&ref=feature%2Fproviders"
                    => GitLabTestHelpers.CreateJsonResponse(
                        new object[]
                        {
                            new { path = "src/Fetcher.cs", type = "blob" },
                            new { path = "docs/notes.md", type = "blob" },
                            new { path = "src", type = "tree" },
                        }),
                "https://gitlab.example.com/api/v4/projects/101/repository/files/src%2FFetcher.cs/raw?ref=feature%2Fproviders"
                    => new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("line1\nline2\nline3\nline4"),
                    },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var factory = new GitLabReviewContextToolsFactory(
            new GitLabConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory,
            gateway,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileSizeBytes = 1024 * 1024 }),
            NullLogger<GitLabReviewContextTools>.Instance);

        var tools = factory.Create(new ReviewContextToolsRequest(review, "feature/providers", 7, clientId, null, host.HostBaseUrl));

        var changedFiles = await tools.GetChangedFilesAsync(CancellationToken.None);
        var tree = await tools.GetFileTreeAsync("main", CancellationToken.None);
        var content = await tools.GetFileContentAsync("src/Fetcher.cs", "main", 2, 3, CancellationToken.None);
        await tools.AskProCursorKnowledgeAsync("where is the fetcher?", CancellationToken.None);
        var symbol = await tools.GetProCursorSymbolInfoAsync("Fetcher", "qualifiedName", 8, CancellationToken.None);

        Assert.Collection(
            changedFiles,
            item =>
            {
                Assert.Equal("src/NewProvider.cs", item.Path);
                Assert.Equal(ChangeType.Rename, item.ChangeType);
            },
            item =>
            {
                Assert.Equal("src/Fetcher.cs", item.Path);
                Assert.Equal(ChangeType.Add, item.ChangeType);
            });
        Assert.Equal(["src/Fetcher.cs", "docs/notes.md"], tree);
        Assert.Equal("line2\nline3", content);
        Assert.Equal("unavailable", symbol.Status);
        Assert.Null(symbol.Symbol);
        await gateway.Received(1)
            .AskKnowledgeAsync(
                Arg.Is<ProCursorKnowledgeQueryRequest>(request =>
                    request.RepositoryContext!.ProviderScopePath == host.HostBaseUrl
                    && request.RepositoryContext.ProviderProjectKey == "acme/platform"
                    && request.RepositoryContext.RepositoryId == "101"
                    && request.RepositoryContext.Branch == "feature/providers"),
                Arg.Any<CancellationToken>());
        await gateway.DidNotReceive()
            .GetSymbolInsightAsync(Arg.Any<ProCursorSymbolQueryRequest>(), Arg.Any<CancellationToken>());
    }
}

public sealed class GitLabCodeReviewPublicationServiceTests
{
    [Fact]
    public async Task PublishReviewAsync_PostsOverviewAndInlineDiscussions()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var repository = new RepositoryRef(host, "101", "acme/platform", "acme/platform/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "4201", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", "start-sha", "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot", "Meister Review Bot", true);
        var result = new ReviewResult(
            "Looks solid overall.",
            [
                new ReviewComment("src/file.ts", 18, CommentSeverity.Warning, "Guard this null case."),
                new ReviewComment(null, null, CommentSeverity.Info, "No blocking issues found."),
            ]);
        var connectionRepository = GitLabTestHelpers.CreateConnectionRepository(clientId, host);

        var postedBodies = new List<(string ContentType, string Body)>();
        var httpClientFactory = GitLabTestHelpers.CreateHttpClientFactory(async request =>
        {
            if (request.RequestUri!.AbsoluteUri == "https://gitlab.example.com/api/v4/user")
            {
                return GitLabTestHelpers.CreateJsonResponse(new { username = "meister-dev" });
            }

            if (request.Method == HttpMethod.Get && request.RequestUri.AbsoluteUri ==
                "https://gitlab.example.com/api/v4/projects/101/merge_requests/42/versions")
            {
                return GitLabTestHelpers.CreateJsonResponse(
                    new object[]
                    {
                        new
                        {
                            id = 7, base_commit_sha = "latest-base-sha", head_commit_sha = "latest-head-sha",
                            start_commit_sha = "latest-start-sha",
                        },
                    });
            }

            if (request.Method == HttpMethod.Post && request.RequestUri.AbsoluteUri ==
                "https://gitlab.example.com/api/v4/projects/101/merge_requests/42/discussions")
            {
                postedBodies.Add(
                    (request.Content!.Headers.ContentType?.MediaType ?? string.Empty,
                        await request.Content.ReadAsStringAsync()));
                return GitLabTestHelpers.CreateJsonResponse(new { id = "discussion-1" }, HttpStatusCode.Created);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new GitLabCodeReviewPublicationService(
            new GitLabConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        await sut.PublishReviewAsync(clientId, review, revision, result, reviewer);

        Assert.Equal(2, postedBodies.Count);
        Assert.All(postedBodies, body => Assert.Equal("multipart/form-data", body.ContentType));

        Assert.Contains("name=body", postedBodies[0].Body, StringComparison.Ordinal);
        Assert.Contains("Looks solid overall.", postedBodies[0].Body, StringComparison.Ordinal);
        Assert.Contains("No blocking issues found.", postedBodies[0].Body, StringComparison.Ordinal);

        Assert.Contains("name=body", postedBodies[1].Body, StringComparison.Ordinal);
        Assert.Contains("Warning: Guard this null case.", postedBodies[1].Body, StringComparison.Ordinal);
        Assert.Contains("position[new_path]", postedBodies[1].Body, StringComparison.Ordinal);
        Assert.Contains("src/file.ts", postedBodies[1].Body, StringComparison.Ordinal);
        Assert.Contains("position[old_path]", postedBodies[1].Body, StringComparison.Ordinal);
        Assert.Contains("position[new_line]", postedBodies[1].Body, StringComparison.Ordinal);
        Assert.Contains("18", postedBodies[1].Body, StringComparison.Ordinal);
        Assert.Contains("position[base_sha]", postedBodies[1].Body, StringComparison.Ordinal);
        Assert.Contains("latest-base-sha", postedBodies[1].Body, StringComparison.Ordinal);
        Assert.Contains("position[head_sha]", postedBodies[1].Body, StringComparison.Ordinal);
        Assert.Contains("latest-head-sha", postedBodies[1].Body, StringComparison.Ordinal);
        Assert.Contains("position[start_sha]", postedBodies[1].Body, StringComparison.Ordinal);
        Assert.Contains("latest-start-sha", postedBodies[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishReviewAsync_WithForbiddenResponse_ThrowsScopeAwareMessage()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var repository = new RepositoryRef(host, "101", "acme/platform", "acme/platform/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "4201", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", "start-sha", "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot", "Meister Review Bot", true);
        var result = new ReviewResult("Looks solid overall.", []);
        var connectionRepository = GitLabTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = GitLabTestHelpers.CreateHttpClientFactory(async request =>
        {
            if (request.RequestUri!.AbsoluteUri == "https://gitlab.example.com/api/v4/user")
            {
                return GitLabTestHelpers.CreateJsonResponse(new { username = "meister-dev" });
            }

            if (request.Method == HttpMethod.Post && request.RequestUri.AbsoluteUri ==
                "https://gitlab.example.com/api/v4/projects/101/merge_requests/42/discussions")
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("{\"message\":\"insufficient_scope\"}"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new GitLabCodeReviewPublicationService(
            new GitLabConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.PublishReviewAsync(clientId, review, revision, result, reviewer));

        Assert.Contains("forbidden", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("api scope", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("insufficient_scope", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublishReviewAsync_WithServerErrorOnInlineDiscussion_ThrowsTargetedFailureMessage()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var repository = new RepositoryRef(host, "101", "acme/platform", "acme/platform/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "4201", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", "start-sha", "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot", "Meister Review Bot", true);
        var result = new ReviewResult(
            "Looks solid overall.",
            [
                new ReviewComment("src/file.ts", 18, CommentSeverity.Warning, "Guard this null case."),
            ]);
        var connectionRepository = GitLabTestHelpers.CreateConnectionRepository(clientId, host);
        var discussionPostCount = 0;
        var httpClientFactory = GitLabTestHelpers.CreateHttpClientFactory(request =>
        {
            if (request.RequestUri!.AbsoluteUri == "https://gitlab.example.com/api/v4/user")
            {
                return Task.FromResult(GitLabTestHelpers.CreateJsonResponse(new { username = "meister-dev" }));
            }

            if (request.Method == HttpMethod.Get && request.RequestUri.AbsoluteUri ==
                "https://gitlab.example.com/api/v4/projects/101/merge_requests/42/versions")
            {
                return Task.FromResult(
                    GitLabTestHelpers.CreateJsonResponse(
                        new object[]
                        {
                            new
                            {
                                id = 8, base_commit_sha = "latest-base-sha", head_commit_sha = "latest-head-sha",
                                start_commit_sha = "latest-start-sha",
                            },
                        }));
            }

            if (request.Method == HttpMethod.Post && request.RequestUri.AbsoluteUri ==
                "https://gitlab.example.com/api/v4/projects/101/merge_requests/42/discussions")
            {
                discussionPostCount++;
                return Task.FromResult(
                    discussionPostCount == 1
                        ? GitLabTestHelpers.CreateJsonResponse(new { id = "discussion-1" }, HttpStatusCode.Created)
                        : new HttpResponseMessage(HttpStatusCode.InternalServerError)
                        {
                            Content = new StringContent("{\"message\":\"500 Internal Server Error\"}"),
                        });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var sut = new GitLabCodeReviewPublicationService(
            new GitLabConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.PublishReviewAsync(clientId, review, revision, result, reviewer));

        Assert.Contains("inline discussion 2/2", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("src/file.ts:L18", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("after 1 successful discussion", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("status 500", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class GitLabReviewDiscoveryProviderTests
{
    [Fact]
    public async Task ListOpenReviewsAsync_WithRequestedReviewer_FiltersToMatchingReviews()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var repository = new RepositoryRef(host, "101", "acme/platform", "acme/platform/propr");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot", "Meister Review Bot", true);
        var connectionRepository = GitLabTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = GitLabTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://gitlab.example.com/api/v4/user" => GitLabTestHelpers.CreateJsonResponse(new { username = "meister-dev" }),
                "https://gitlab.example.com/api/v4/projects/101/merge_requests?state=opened&per_page=100" =>
                    GitLabTestHelpers.CreateJsonResponse(
                        new object[]
                        {
                            new
                            {
                                id = 4201,
                                iid = 42,
                                title = "Provider neutral adapters",
                                web_url = "https://gitlab.example.com/acme/platform/propr/-/merge_requests/42",
                                state = "opened",
                                draft = false,
                                source_branch = "feature/providers",
                                target_branch = "main",
                                sha = "head-sha",
                                diff_refs = new
                                    { base_sha = "base-sha", head_sha = "head-sha", start_sha = "start-sha" },
                                reviewers = new object[]
                                {
                                    new
                                    {
                                        id = 99, username = "meister-review-bot", name = "Meister Review Bot",
                                        bot = true,
                                    },
                                },
                            },
                            new
                            {
                                id = 4301,
                                iid = 43,
                                title = "Unassigned review",
                                web_url = "https://gitlab.example.com/acme/platform/propr/-/merge_requests/43",
                                state = "opened",
                                draft = false,
                                source_branch = "feature/other",
                                target_branch = "main",
                                sha = "head-sha-2",
                                diff_refs = new
                                    { base_sha = "base-sha-2", head_sha = "head-sha-2", start_sha = "start-sha-2" },
                                reviewers = new object[]
                                {
                                    new { id = 12, username = "other-reviewer", name = "Other Reviewer", bot = false },
                                },
                            },
                        }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        var sut = new GitLabReviewDiscoveryProvider(
            new GitLabConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var result = await sut.ListOpenReviewsAsync(clientId, repository, reviewer);

        var item = Assert.Single(result);
        Assert.Equal(42, item.CodeReview.Number);
        Assert.Equal(CodeReviewState.Open, item.ReviewState);
        Assert.Equal("head-sha", item.ReviewRevision!.HeadSha);
        Assert.Equal("base-sha", item.ReviewRevision.BaseSha);
        Assert.Equal("meister-review-bot", item.RequestedReviewerIdentity!.Login);
    }
}

public sealed class GitLabReviewThreadStatusProviderTests
{
    [Fact]
    public async Task GetReviewerThreadStatusesAsync_ReviewerOwnedDiffThreads_ReturnsHistoryAndReplyCounts()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var connectionRepository = GitLabTestHelpers.CreateConnectionRepository(clientId, host);
        var clientRegistry = Substitute.For<IClientRegistry>();
        clientRegistry.GetReviewerIdentityAsync(clientId, host, Arg.Any<CancellationToken>())
            .Returns(new ReviewerIdentity(host, "99", "meister-review-bot", "Meister Review Bot", true));
        var httpClientFactory = GitLabTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://gitlab.example.com/api/v4/user" => GitLabTestHelpers.CreateJsonResponse(new { username = "meister-dev" }),
                "https://gitlab.example.com/api/v4/projects/101/merge_requests/42/discussions?per_page=100" =>
                    GitLabTestHelpers.CreateJsonResponse(
                        new object[]
                        {
                            new
                            {
                                individual_note = false,
                                notes = new object[]
                                {
                                    new
                                    {
                                        id = 501,
                                        body = "Please handle null.",
                                        system = false,
                                        resolved = false,
                                        author = new { id = 99, username = "meister-review-bot" },
                                        position = new { new_path = "src/feature.ts", old_path = "src/feature.ts" },
                                    },
                                    new
                                    {
                                        id = 502,
                                        body = "Done.",
                                        system = false,
                                        resolved = true,
                                        author = new { id = 7, username = "octocat" },
                                        position = new { new_path = "src/feature.ts", old_path = "src/feature.ts" },
                                    },
                                },
                            },
                            new
                            {
                                individual_note = true,
                                notes = new object[]
                                {
                                    new
                                    {
                                        id = 601,
                                        body = "Overview note.",
                                        system = false,
                                        resolved = false,
                                        author = new { id = 99, username = "meister-review-bot" },
                                        position = (object?)null,
                                    },
                                },
                            },
                        }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        var sut = new GitLabReviewThreadStatusProvider(
            new GitLabConnectionVerifier(connectionRepository, httpClientFactory),
            clientRegistry,
            httpClientFactory);

        var result = await sut.GetReviewerThreadStatusesAsync(
            "https://gitlab.example.com",
            "acme/platform",
            "101",
            42,
            Guid.Empty,
            clientId,
            CancellationToken.None);

        var entry = Assert.Single(result);
        Assert.Equal(501, entry.ThreadId);
        Assert.Equal("Fixed", entry.Status);
        Assert.Equal("src/feature.ts", entry.FilePath);
        Assert.Equal(1, entry.NonReviewerReplyCount);
        Assert.Contains("meister-review-bot: Please handle null.", entry.CommentHistory);
        Assert.Contains("octocat: Done.", entry.CommentHistory);
    }
}

internal static class GitLabTestHelpers
{
    public static IClientScmConnectionRepository CreateConnectionRepository(
        Guid clientId,
        ProviderHostRef host,
        string secret = "glpat-test")
    {
        var repository = Substitute.For<IClientScmConnectionRepository>();
        repository.GetOperationalConnectionAsync(clientId, host, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    Guid.NewGuid(),
                    clientId,
                    ScmProvider.GitLab,
                    host.HostBaseUrl,
                    ScmAuthenticationKind.PersonalAccessToken,
                    "GitLab",
                    secret,
                    true));
        return repository;
    }

    public static IHttpClientFactory CreateHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("GitLabProvider")
            .Returns(new HttpClient(new StubHttpMessageHandler(request => Task.FromResult(responder(request)))));
        return factory;
    }

    public static IHttpClientFactory CreateHttpClientFactory(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("GitLabProvider").Returns(new HttpClient(new StubHttpMessageHandler(responder)));
        return factory;
    }

    public static HttpResponseMessage CreateJsonResponse<T>(T payload, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload)),
        };
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return responder(request);
        }
    }
}

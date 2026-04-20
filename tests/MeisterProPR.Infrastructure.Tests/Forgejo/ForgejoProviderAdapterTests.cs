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
using MeisterProPR.Infrastructure.Features.Providers.Forgejo.Discovery;
using MeisterProPR.Infrastructure.Features.Providers.Forgejo.Identity;
using MeisterProPR.Infrastructure.Features.Providers.Forgejo.Reviewing;
using MeisterProPR.Infrastructure.Features.Providers.Forgejo.Security;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.Forgejo;

public sealed class ForgejoConnectionVerifierTests
{
    [Fact]
    public async Task VerifyAsync_ValidPersonalAccessToken_ReturnsAuthenticatedUsername()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://codeberg.example.com");
        var repository = ForgejoTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = ForgejoTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://codeberg.example.com/api/v1/user" => ForgejoTestHelpers.CreateJsonResponse(new { login = "meister-dev" }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var sut = new ForgejoConnectionVerifier(repository, httpClientFactory);

        var result = await sut.VerifyAsync(clientId, host);

        Assert.Equal("meister-dev", result.AuthenticatedUsername);
        Assert.Equal("forgejo-token", result.Connection.Secret);
    }
}

public sealed class ForgejoDiscoveryServiceTests
{
    [Fact]
    public async Task ListScopesAsync_IncludesAuthenticatedUserAndOrganizations()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://codeberg.example.com");
        var connectionRepository = ForgejoTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = ForgejoTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://codeberg.example.com/api/v1/user" => ForgejoTestHelpers.CreateJsonResponse(new { login = "meister-dev" }),
                "https://codeberg.example.com/api/v1/user/orgs?limit=100" => ForgejoTestHelpers.CreateJsonResponse(
                    new[]
                    {
                        new { username = "acme", name = "Acme" },
                        new { username = "acme-platform", name = "Acme Platform" },
                    }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var sut = new ForgejoDiscoveryService(
            new ForgejoConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var scopes = await sut.ListScopesAsync(clientId, host);

        Assert.Equal(["acme", "acme-platform", "meister-dev"], scopes);
    }

    [Fact]
    public async Task ListRepositoriesAsync_OrganizationScope_ReturnsNormalizedRepositoryReferences()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://codeberg.example.com");
        var connectionRepository = ForgejoTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = ForgejoTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://codeberg.example.com/api/v1/user" => ForgejoTestHelpers.CreateJsonResponse(new { login = "meister-dev" }),
                "https://codeberg.example.com/api/v1/orgs/acme/repos?limit=100" =>
                    ForgejoTestHelpers.CreateJsonResponse(
                        new[]
                        {
                            new { id = 101, full_name = "acme/propr", owner = new { login = "acme" } },
                            new { id = 102, full_name = "acme/platform", owner = new { login = "acme" } },
                        }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var sut = new ForgejoDiscoveryService(
            new ForgejoConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var repositories = await sut.ListRepositoriesAsync(clientId, host, "acme");

        Assert.Equal(2, repositories.Count);
        Assert.Equal("101", repositories[0].ExternalRepositoryId);
        Assert.Equal("acme", repositories[0].OwnerOrNamespace);
        Assert.Equal("acme/propr", repositories[0].ProjectPath);
    }
}

public sealed class ForgejoReviewerIdentityServiceTests
{
    [Fact]
    public async Task ResolveCandidatesAsync_ReturnsSortedReviewerIdentities()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://codeberg.example.com");
        var connectionRepository = ForgejoTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = ForgejoTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://codeberg.example.com/api/v1/user" => ForgejoTestHelpers.CreateJsonResponse(new { login = "meister-dev" }),
                "https://codeberg.example.com/api/v1/users/search?q=meister&limit=20" => ForgejoTestHelpers
                    .CreateJsonResponse(
                        new
                        {
                            ok = true,
                            data = new object[]
                            {
                                new { id = 2, login = "meister-review-bot", full_name = "Meister Review Bot" },
                                new { id = 1, login = "meister-dev", full_name = "Meister Dev" },
                            },
                        }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var sut = new ForgejoReviewerIdentityService(
            new ForgejoConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var candidates = await sut.ResolveCandidatesAsync(clientId, host, "meister");

        Assert.Equal(2, candidates.Count);
        Assert.Equal("meister-dev", candidates[0].Login);
        Assert.False(candidates[0].IsBot);
        Assert.True(candidates[1].IsBot);
    }
}

public sealed class ForgejoCodeReviewQueryServiceTests
{
    [Fact]
    public async Task GetReviewAsync_ReturnsNormalizedReviewMetadataAndRevision()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://codeberg.example.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "4201", 42);
        var connectionRepository = ForgejoTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = ForgejoTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://codeberg.example.com/api/v1/user" => ForgejoTestHelpers.CreateJsonResponse(new { login = "meister-dev" }),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42" =>
                    ForgejoTestHelpers.CreateJsonResponse(
                        new
                        {
                            id = 4201,
                            number = 42,
                            title = "Add provider-neutral adapters",
                            html_url = "https://codeberg.example.com/acme/propr/pulls/42",
                            state = "open",
                            draft = false,
                            merged = false,
                            head = new { @ref = "feature/providers", sha = "head-sha" },
                            @base = new { @ref = "main", sha = "base-sha" },
                            requested_reviewers = new object[]
                            {
                                new { id = 99, login = "meister-review-bot", full_name = "Meister Review Bot" },
                            },
                        }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var sut = new ForgejoCodeReviewQueryService(
            new ForgejoConnectionVerifier(connectionRepository, httpClientFactory),
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

public sealed class ForgejoPullRequestFetcherTests
{
    [Fact]
    public async Task FetchAsync_ReturnsChangedFilesAndThreads()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://codeberg.example.com");
        var connectionRepository = ForgejoTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = ForgejoTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://codeberg.example.com/api/v1/user" => ForgejoTestHelpers.CreateJsonResponse(new { login = "meister-dev" }),
                "https://codeberg.example.com/api/v1/repositories/101" => ForgejoTestHelpers.CreateJsonResponse(new { full_name = "acme/propr" }),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42" =>
                    ForgejoTestHelpers.CreateJsonResponse(
                        new
                        {
                            title = "Add provider-neutral fetchers",
                            body = "Fetch Forgejo pull requests without Azure-only code.",
                            state = "open",
                            merged = false,
                            head = new { @ref = "feature/providers", sha = "head-sha" },
                            @base = new { @ref = "main", sha = "base-sha" },
                        }),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/files?limit=100" => ForgejoTestHelpers
                    .CreateJsonResponse(
                        new object[]
                        {
                            new
                            {
                                filename = "src/NewProvider.cs", status = "renamed",
                                previous_filename = "src/LegacyProvider.cs", patch = "rename diff",
                            },
                            new
                            {
                                filename = "src/Fetcher.cs", status = "added", previous_filename = (string?)null,
                                patch = "+class Fetcher",
                            },
                        }),
                "https://codeberg.example.com/api/v1/repos/acme/propr/contents/src%2FNewProvider.cs?ref=head-sha" =>
                    ForgejoTestHelpers.CreateJsonResponse(
                        new
                        {
                            content = Convert.ToBase64String(Encoding.UTF8.GetBytes("public class NewProvider {}")),
                            encoding = "base64",
                        }),
                "https://codeberg.example.com/api/v1/repos/acme/propr/contents/src%2FLegacyProvider.cs?ref=base-sha" =>
                    ForgejoTestHelpers.CreateJsonResponse(
                        new
                        {
                            content = Convert.ToBase64String(Encoding.UTF8.GetBytes("public class LegacyProvider {}")),
                            encoding = "base64",
                        }),
                "https://codeberg.example.com/api/v1/repos/acme/propr/contents/src%2FFetcher.cs?ref=head-sha" =>
                    ForgejoTestHelpers.CreateJsonResponse(
                        new
                        {
                            content = Convert.ToBase64String(Encoding.UTF8.GetBytes("public class Fetcher {}")),
                            encoding = "base64",
                        }),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews?limit=100" => ForgejoTestHelpers
                    .CreateJsonResponse(
                        new object[]
                        {
                            new { id = 7001, state = "COMMENT" },
                        }),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews/7001/comments" =>
                    ForgejoTestHelpers.CreateJsonResponse(
                        new object[]
                        {
                            new
                            {
                                id = 501, body = "Please handle null.", path = "src/Fetcher.cs", position = 18,
                                original_position = (int?)null, created_at = "2026-04-17T10:00:00Z",
                                user = new { id = 99, login = "meister-review-bot" },
                            },
                        }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var sut = new ForgejoPullRequestFetcher(
            new ForgejoConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var result = await sut.FetchAsync(
            "https://codeberg.example.com",
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
    public async Task FetchAsync_PathBasedRepositoryIdentifier_SkipsRepositoryLookup()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://codeberg.example.com");
        var connectionRepository = ForgejoTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = ForgejoTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://codeberg.example.com/api/v1/user" => ForgejoTestHelpers.CreateJsonResponse(new { login = "meister-dev" }),
                "https://codeberg.example.com/api/v1/repos/local_admin/propr/pulls/42" => ForgejoTestHelpers
                    .CreateJsonResponse(
                        new
                        {
                            title = "Fetch path-based Forgejo identifiers",
                            body = "Support owner/repo identifiers without numeric repository ids.",
                            state = "open",
                            merged = false,
                            head = new { @ref = "feature/providers", sha = "head-sha" },
                            @base = new { @ref = "main", sha = "base-sha" },
                        }),
                "https://codeberg.example.com/api/v1/repos/local_admin/propr/pulls/42/files?limit=100" =>
                    ForgejoTestHelpers.CreateJsonResponse(Array.Empty<object>()),
                "https://codeberg.example.com/api/v1/repos/local_admin/propr/pulls/42/reviews?limit=100" =>
                    ForgejoTestHelpers.CreateJsonResponse(Array.Empty<object>()),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var sut = new ForgejoPullRequestFetcher(
            new ForgejoConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var result = await sut.FetchAsync(
            "https://codeberg.example.com",
            "local_admin",
            "local_admin/propr",
            42,
            7,
            clientId: clientId,
            cancellationToken: CancellationToken.None);

        Assert.Equal("Fetch path-based Forgejo identifiers", result.Title);
        Assert.Equal("feature/providers", result.SourceBranch);
        Assert.Empty(result.ChangedFiles);
    }
}

public sealed class ForgejoReviewContextToolsTests
{
    [Fact]
    public async Task FactoryCreatedTools_ReturnChangedFilesTreeContentAndProCursorContext()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://codeberg.example.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "4201", 42);
        var connectionRepository = ForgejoTestHelpers.CreateConnectionRepository(clientId, host);
        var gateway = Substitute.For<IProCursorGateway>();
        gateway.AskKnowledgeAsync(Arg.Any<ProCursorKnowledgeQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ProCursorKnowledgeAnswerDto("ok", []));
        var httpClientFactory = ForgejoTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://codeberg.example.com/api/v1/user" => ForgejoTestHelpers.CreateJsonResponse(new { login = "meister-dev" }),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/files?limit=100" => ForgejoTestHelpers
                    .CreateJsonResponse(
                        new object[]
                        {
                            new { filename = "src/Fetcher.cs", status = "added" },
                            new { filename = "src/NewProvider.cs", status = "renamed" },
                        }),
                "https://codeberg.example.com/api/v1/repos/acme/propr/branches/feature%2Fproviders" =>
                    ForgejoTestHelpers.CreateJsonResponse(new { commit = new { id = "head-sha" } }),
                "https://codeberg.example.com/api/v1/repos/acme/propr/git/trees/head-sha?recursive=true" =>
                    ForgejoTestHelpers.CreateJsonResponse(
                        new
                        {
                            tree = new object[]
                            {
                                new { path = "src/Fetcher.cs", type = "blob" },
                                new { path = "docs/notes.md", type = "blob" },
                                new { path = "src", type = "tree" },
                            },
                        }),
                "https://codeberg.example.com/api/v1/repos/acme/propr/contents/src%2FFetcher.cs?ref=feature%2Fproviders"
                    => ForgejoTestHelpers.CreateJsonResponse(
                        new
                        {
                            content = Convert.ToBase64String(Encoding.UTF8.GetBytes("line1\nline2\nline3\nline4")),
                            encoding = "base64",
                        }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var factory = new ForgejoReviewContextToolsFactory(
            new ForgejoConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory,
            gateway,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileSizeBytes = 1024 * 1024 }),
            NullLogger<ForgejoReviewContextTools>.Instance);

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
}

public sealed class ForgejoCodeReviewPublicationServiceTests
{
    [Fact]
    public async Task PublishReviewAsync_PostsOneReviewWithSummaryAndInlineComments()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://codeberg.example.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "4201", 42);
        var revision = new ReviewRevision(
            "aabbccddeeff00112233445566778899aabbccdd",
            "00112233445566778899aabbccddeeff00112233",
            "00112233445566778899aabbccddeeff00112233",
            "aabbccddeeff00112233445566778899aabbccdd",
            "00112233445566778899aabbccddeeff00112233...aabbccddeeff00112233445566778899aabbccdd");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot", "Meister Review Bot", true);
        var result = new ReviewResult(
            "Looks solid overall.",
            [
                new ReviewComment("src/file.ts", 18, CommentSeverity.Warning, "Guard this null case."),
                new ReviewComment(null, null, CommentSeverity.Info, "No blocking issues found."),
            ]);
        var connectionRepository = ForgejoTestHelpers.CreateConnectionRepository(clientId, host);

        string? postedBody = null;
        var httpClientFactory = ForgejoTestHelpers.CreateHttpClientFactory(async request =>
        {
            if (request.RequestUri!.AbsoluteUri == "https://codeberg.example.com/api/v1/user")
            {
                return ForgejoTestHelpers.CreateJsonResponse(new { login = "meister-dev" });
            }

            if (request.Method == HttpMethod.Get && request.RequestUri.AbsoluteUri ==
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews?limit=100")
            {
                return ForgejoTestHelpers.CreateJsonResponse(Array.Empty<object>());
            }

            if (request.Method == HttpMethod.Post && request.RequestUri.AbsoluteUri ==
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews")
            {
                postedBody = await request.Content!.ReadAsStringAsync();
                return ForgejoTestHelpers.CreateJsonResponse(new { id = 9001 });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new ForgejoCodeReviewPublicationService(
            new ForgejoConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        await sut.PublishReviewAsync(clientId, review, revision, result, reviewer);

        Assert.NotNull(postedBody);
        using var document = JsonDocument.Parse(postedBody);
        Assert.Equal(
            "aabbccddeeff00112233445566778899aabbccdd",
            document.RootElement.GetProperty("commit_id").GetString());
        Assert.Equal("COMMENT", document.RootElement.GetProperty("event").GetString());
        Assert.Contains(
            "Looks solid overall.",
            document.RootElement.GetProperty("body").GetString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "No blocking issues found.",
            document.RootElement.GetProperty("body").GetString(),
            StringComparison.Ordinal);
        var comments = document.RootElement.GetProperty("comments");
        Assert.Equal(1, comments.GetArrayLength());
        Assert.Equal("src/file.ts", comments[0].GetProperty("path").GetString());
        Assert.Equal(18, comments[0].GetProperty("new_position").GetInt32());
    }

    [Fact]
    public async Task PublishReviewAsync_WithInvalidRevision_OmitsCommitIdFromPayload()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://codeberg.example.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "4201", 42);
        var revision = new ReviewRevision(
            "review_requested-head-sha",
            "base-sha",
            "base-sha",
            "review_requested-head-sha",
            "base-sha...review_requested-head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot", "Meister Review Bot", true);
        var result = new ReviewResult(
            "Looks solid overall.",
            [new ReviewComment("src/file.ts", 18, CommentSeverity.Warning, "Guard this null case.")]);
        var connectionRepository = ForgejoTestHelpers.CreateConnectionRepository(clientId, host);

        string? postedBody = null;
        var httpClientFactory = ForgejoTestHelpers.CreateHttpClientFactory(async request =>
        {
            if (request.RequestUri!.AbsoluteUri == "https://codeberg.example.com/api/v1/user")
            {
                return ForgejoTestHelpers.CreateJsonResponse(new { login = "meister-dev" });
            }

            if (request.Method == HttpMethod.Get && request.RequestUri.AbsoluteUri ==
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews?limit=100")
            {
                return ForgejoTestHelpers.CreateJsonResponse(Array.Empty<object>());
            }

            if (request.Method == HttpMethod.Post && request.RequestUri.AbsoluteUri ==
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews")
            {
                postedBody = await request.Content!.ReadAsStringAsync();
                return ForgejoTestHelpers.CreateJsonResponse(new { id = 9001 });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new ForgejoCodeReviewPublicationService(
            new ForgejoConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        await sut.PublishReviewAsync(clientId, review, revision, result, reviewer);

        Assert.NotNull(postedBody);
        using var document = JsonDocument.Parse(postedBody);
        Assert.False(document.RootElement.TryGetProperty("commit_id", out _));
        Assert.Equal("COMMENT", document.RootElement.GetProperty("event").GetString());
    }

    [Fact]
    public async Task PublishReviewAsync_WithExistingPendingReview_DeletesPendingReviewBeforePosting()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://codeberg.example.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "4201", 42);
        var revision = new ReviewRevision(
            "aabbccddeeff00112233445566778899aabbccdd",
            "00112233445566778899aabbccddeeff00112233",
            "00112233445566778899aabbccddeeff00112233",
            "aabbccddeeff00112233445566778899aabbccdd",
            "00112233445566778899aabbccddeeff00112233...aabbccddeeff00112233445566778899aabbccdd");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot", "Meister Review Bot", true);
        var result = new ReviewResult(
            "Looks solid overall.",
            [new ReviewComment("src/file.ts", 18, CommentSeverity.Warning, "Guard this null case.")]);
        var connectionRepository = ForgejoTestHelpers.CreateConnectionRepository(clientId, host);

        var requests = new List<string>();
        var httpClientFactory = ForgejoTestHelpers.CreateHttpClientFactory(async request =>
        {
            requests.Add($"{request.Method} {request.RequestUri!.AbsoluteUri}");

            if (request.RequestUri!.AbsoluteUri == "https://codeberg.example.com/api/v1/user")
            {
                return ForgejoTestHelpers.CreateJsonResponse(new { login = "meister-dev" });
            }

            if (request.Method == HttpMethod.Get && request.RequestUri.AbsoluteUri ==
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews?limit=100")
            {
                return ForgejoTestHelpers.CreateJsonResponse(
                    new object[]
                    {
                        new { id = 7001, state = "PENDING", user = new { id = 99, login = "meister-review-bot" } },
                        new { id = 7002, state = "COMMENT", user = new { id = 99, login = "meister-review-bot" } },
                    });
            }

            if (request.Method == HttpMethod.Delete && request.RequestUri.AbsoluteUri ==
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews/7001")
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (request.Method == HttpMethod.Post && request.RequestUri.AbsoluteUri ==
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews")
            {
                return ForgejoTestHelpers.CreateJsonResponse(new { id = 9001 });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new ForgejoCodeReviewPublicationService(
            new ForgejoConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        await sut.PublishReviewAsync(clientId, review, revision, result, reviewer);

        Assert.Equal(
            [
                "GET https://codeberg.example.com/api/v1/user",
                "GET https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews?limit=100",
                "DELETE https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews/7001",
                "POST https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews",
            ],
            requests);
    }

    [Fact]
    public async Task PublishReviewAsync_FailureIncludesResponseBody()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://codeberg.example.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "4201", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", "base-sha", "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot", "Meister Review Bot", true);
        var result = new ReviewResult(
            "Looks solid overall.",
            [new ReviewComment("src/file.ts", 18, CommentSeverity.Warning, "Guard this null case.")]);
        var connectionRepository = ForgejoTestHelpers.CreateConnectionRepository(clientId, host);

        var httpClientFactory = ForgejoTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://codeberg.example.com/api/v1/user" => ForgejoTestHelpers.CreateJsonResponse(new { login = "meister-dev" }),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews?limit=100" => ForgejoTestHelpers
                    .CreateJsonResponse(Array.Empty<object>()),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews" => new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("invalid line anchor for diff"),
                },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var sut = new ForgejoCodeReviewPublicationService(
            new ForgejoConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.PublishReviewAsync(clientId, review, revision, result, reviewer));

        Assert.Contains("500", error.Message, StringComparison.Ordinal);
        Assert.Contains("invalid line anchor for diff", error.Message, StringComparison.Ordinal);
    }
}

public sealed class ForgejoReviewDiscoveryProviderTests
{
    [Fact]
    public async Task ListOpenReviewsAsync_WithRequestedReviewer_FiltersToMatchingReviews()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://codeberg.example.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot", "Meister Review Bot", true);
        var connectionRepository = ForgejoTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = ForgejoTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://codeberg.example.com/api/v1/user" => ForgejoTestHelpers.CreateJsonResponse(new { login = "meister-dev" }),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls?state=open&limit=100" => ForgejoTestHelpers
                    .CreateJsonResponse(
                        new object[]
                        {
                            new
                            {
                                id = 4201,
                                number = 42,
                                title = "Provider neutral adapters",
                                html_url = "https://codeberg.example.com/acme/propr/pulls/42",
                                state = "open",
                                merged = false,
                                head = new { @ref = "feature/providers", sha = "head-sha" },
                                @base = new { @ref = "main", sha = "base-sha" },
                                requested_reviewers = new object[]
                                {
                                    new { id = 99, login = "meister-review-bot", full_name = "Meister Review Bot" },
                                },
                            },
                            new
                            {
                                id = 4301,
                                number = 43,
                                title = "Unassigned review",
                                html_url = "https://codeberg.example.com/acme/propr/pulls/43",
                                state = "open",
                                merged = false,
                                head = new { @ref = "feature/other", sha = "head-sha-2" },
                                @base = new { @ref = "main", sha = "base-sha-2" },
                                requested_reviewers = new object[]
                                {
                                    new { id = 12, login = "other-reviewer", full_name = "Other Reviewer" },
                                },
                            },
                        }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        var sut = new ForgejoReviewDiscoveryProvider(
            new ForgejoConnectionVerifier(connectionRepository, httpClientFactory),
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

public sealed class ForgejoReviewThreadStatusProviderTests
{
    [Fact]
    public async Task GetReviewerThreadStatusesAsync_ReviewerOwnedAnchors_ReturnsHistoryAndReplyCounts()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://codeberg.example.com");
        var connectionRepository = ForgejoTestHelpers.CreateConnectionRepository(clientId, host);
        var clientRegistry = Substitute.For<IClientRegistry>();
        clientRegistry.GetReviewerIdentityAsync(clientId, host, Arg.Any<CancellationToken>())
            .Returns(new ReviewerIdentity(host, "99", "meister-review-bot", "Meister Review Bot", true));
        var httpClientFactory = ForgejoTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://codeberg.example.com/api/v1/user" => ForgejoTestHelpers.CreateJsonResponse(new { login = "meister-dev" }),
                "https://codeberg.example.com/api/v1/repositories/101" => ForgejoTestHelpers.CreateJsonResponse(new { full_name = "acme/propr" }),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews?limit=100" => ForgejoTestHelpers
                    .CreateJsonResponse(
                        new object[]
                        {
                            new
                            {
                                id = 7001, state = "COMMENT",
                                user = new { id = 99, login = "meister-review-bot", full_name = "Meister Review Bot" },
                            },
                            new
                            {
                                id = 7002, state = "COMMENT",
                                user = new { id = 7, login = "octocat", full_name = "Octocat" },
                            },
                        }),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews/7001/comments" =>
                    ForgejoTestHelpers.CreateJsonResponse(
                        new object[]
                        {
                            new
                            {
                                id = 501, body = "Please handle null.", path = "src/feature.ts", position = 18,
                                user = new { id = 99, login = "meister-review-bot", full_name = "Meister Review Bot" },
                                created_at = "2026-04-14T08:00:00Z",
                            },
                        }),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews/7002/comments" =>
                    ForgejoTestHelpers.CreateJsonResponse(
                        new object[]
                        {
                            new
                            {
                                id = 502, body = "Done.", path = "src/feature.ts", position = 18,
                                user = new { id = 7, login = "octocat", full_name = "Octocat" },
                                created_at = "2026-04-14T08:01:00Z",
                            },
                        }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        var sut = new ForgejoReviewThreadStatusProvider(
            new ForgejoConnectionVerifier(connectionRepository, httpClientFactory),
            clientRegistry,
            httpClientFactory);

        var result = await sut.GetReviewerThreadStatusesAsync(
            "https://codeberg.example.com",
            "acme",
            "101",
            42,
            Guid.Empty,
            clientId,
            CancellationToken.None);

        var entry = Assert.Single(result);
        Assert.Equal(501, entry.ThreadId);
        Assert.Equal("Active", entry.Status);
        Assert.Equal("src/feature.ts", entry.FilePath);
        Assert.Equal(1, entry.NonReviewerReplyCount);
        Assert.Contains("meister-review-bot: Please handle null.", entry.CommentHistory);
        Assert.Contains("octocat: Done.", entry.CommentHistory);
    }

    [Fact]
    public async Task GetReviewerThreadStatusesAsync_PathBasedRepositoryIdentifier_SkipsRepositoryLookup()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://codeberg.example.com");
        var connectionRepository = ForgejoTestHelpers.CreateConnectionRepository(clientId, host);
        var clientRegistry = Substitute.For<IClientRegistry>();
        clientRegistry.GetReviewerIdentityAsync(clientId, host, Arg.Any<CancellationToken>())
            .Returns(new ReviewerIdentity(host, "99", "meister-review-bot", "Meister Review Bot", true));
        var httpClientFactory = ForgejoTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://codeberg.example.com/api/v1/user" => ForgejoTestHelpers.CreateJsonResponse(new { login = "meister-dev" }),
                "https://codeberg.example.com/api/v1/repos/local_admin/propr/pulls/42/reviews?limit=100" =>
                    ForgejoTestHelpers.CreateJsonResponse(
                        new object[]
                        {
                            new
                            {
                                id = 7001, state = "COMMENT",
                                user = new { id = 99, login = "meister-review-bot", full_name = "Meister Review Bot" },
                            },
                            new
                            {
                                id = 7002, state = "COMMENT",
                                user = new { id = 7, login = "octocat", full_name = "Octocat" },
                            },
                        }),
                "https://codeberg.example.com/api/v1/repos/local_admin/propr/pulls/42/reviews/7001/comments" =>
                    ForgejoTestHelpers.CreateJsonResponse(
                        new object[]
                        {
                            new
                            {
                                id = 501, body = "Please handle null.", path = "src/feature.ts", position = 18,
                                user = new { id = 99, login = "meister-review-bot", full_name = "Meister Review Bot" },
                                created_at = "2026-04-14T08:00:00Z",
                            },
                        }),
                "https://codeberg.example.com/api/v1/repos/local_admin/propr/pulls/42/reviews/7002/comments" =>
                    ForgejoTestHelpers.CreateJsonResponse(
                        new object[]
                        {
                            new
                            {
                                id = 502, body = "Done.", path = "src/feature.ts", position = 18,
                                user = new { id = 7, login = "octocat", full_name = "Octocat" },
                                created_at = "2026-04-14T08:01:00Z",
                            },
                        }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        var sut = new ForgejoReviewThreadStatusProvider(
            new ForgejoConnectionVerifier(connectionRepository, httpClientFactory),
            clientRegistry,
            httpClientFactory);

        var result = await sut.GetReviewerThreadStatusesAsync(
            "https://codeberg.example.com",
            "local_admin",
            "local_admin/propr",
            42,
            Guid.Empty,
            clientId,
            CancellationToken.None);

        var entry = Assert.Single(result);
        Assert.Equal(501, entry.ThreadId);
        Assert.Equal("src/feature.ts", entry.FilePath);
        Assert.Equal(1, entry.NonReviewerReplyCount);
    }
}

internal static class ForgejoTestHelpers
{
    public static IClientScmConnectionRepository CreateConnectionRepository(
        Guid clientId,
        ProviderHostRef host,
        string secret = "forgejo-token")
    {
        var repository = Substitute.For<IClientScmConnectionRepository>();
        repository.GetOperationalConnectionAsync(clientId, host, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    Guid.NewGuid(),
                    clientId,
                    ScmProvider.Forgejo,
                    host.HostBaseUrl,
                    ScmAuthenticationKind.PersonalAccessToken,
                    "Forgejo",
                    secret,
                    true));
        return repository;
    }

    public static IHttpClientFactory CreateHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("ForgejoProvider")
            .Returns(new HttpClient(new StubHttpMessageHandler(request => Task.FromResult(responder(request)))));
        return factory;
    }

    public static IHttpClientFactory CreateHttpClientFactory(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("ForgejoProvider").Returns(new HttpClient(new StubHttpMessageHandler(responder)));
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

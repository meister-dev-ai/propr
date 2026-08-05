// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Security;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Security;
using MeisterDev.ProPR.Infrastructure.Tests.Forgejo;
using MeisterDev.ProPR.Infrastructure.Tests.GitLab;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers;

/// <summary>
///     Each provider read that spans more than one page, proven against the pagination its host actually
///     speaks. The pagers themselves are covered separately: what these prove is that each call site follows the
///     signal its provider sends, so nothing drops out of a review's scope or its conversation unannounced.
/// </summary>
public sealed class ProviderCollectionPaginationTests
{
    [Fact]
    public async Task GitHubChangedFiles_SpanMoreThanOnePage_EveryFileIsInScope()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var httpClientFactory = CreateGitHubHttpClientFactory(request => request.RequestUri!.AbsoluteUri switch
        {
            "https://api.github.com/user" => CreateJsonResponse(new { login = "meister-dev" }),
            "https://api.github.com/repos/acme/propr/pulls/42" => CreateJsonResponse(
                new
                {
                    title = "Add assets",
                    body = "Two pages of changed files.",
                    state = "open",
                    merged_at = (string?)null,
                    head = new { @ref = "feature/assets", sha = "head-sha" },
                    @base = new { @ref = "main", sha = "base-sha" },
                }),

            // Page one advertises the next in its Link header, exactly as GitHub does.
            "https://api.github.com/repos/acme/propr/pulls/42/files?per_page=100" => WithLinkToNextPage(
                CreateJsonResponse(
                    new object[]
                    {
                        new { filename = "assets/first.png", status = "added", patch = (string?)null },
                    })),
            "https://api.github.com/repos/acme/propr/pulls/42/files?per_page=100&page=2" => CreateJsonResponse(
                new object[]
                {
                    new { filename = "assets/second.png", status = "added", patch = (string?)null },
                }),
            "https://api.github.com/graphql" => CreateJsonResponse(
                new { data = new { repository = new { pullRequest = new { reviewThreads = new { nodes = Array.Empty<object>() } } } } }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var sut = new GitHubPullRequestFetcher(
            new GitHubConnectionVerifier(CreateConnectionRepository(clientId, host, ScmProvider.GitHub), httpClientFactory),
            httpClientFactory);

        var result = await sut.FetchAsync(
            "https://github.com",
            "acme",
            "acme/propr",
            42,
            7,
            clientId: clientId,
            cancellationToken: CancellationToken.None);

        Assert.Equal(
            ["assets/first.png", "assets/second.png"],
            result.ChangedFiles.Select(file => file.Path));
    }

    [Fact]
    public async Task GitHubChangedFiles_FitInOnePage_AreReadInOneRequest()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var fileRequests = new List<string>();
        var httpClientFactory = CreateGitHubHttpClientFactory(request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri.Contains("/files", StringComparison.Ordinal))
            {
                fileRequests.Add(uri);
            }

            return uri switch
            {
                "https://api.github.com/user" => CreateJsonResponse(new { login = "meister-dev" }),
                "https://api.github.com/repos/acme/propr/pulls/42" => CreateJsonResponse(
                    new
                    {
                        title = "Add one asset",
                        body = "One page of changed files.",
                        state = "open",
                        merged_at = (string?)null,
                        head = new { @ref = "feature/assets", sha = "head-sha" },
                        @base = new { @ref = "main", sha = "base-sha" },
                    }),
                "https://api.github.com/repos/acme/propr/pulls/42/files?per_page=100" => CreateJsonResponse(
                    new object[]
                    {
                        new { filename = "assets/only.png", status = "added", patch = (string?)null },
                    }),
                "https://api.github.com/graphql" => CreateJsonResponse(
                    new { data = new { repository = new { pullRequest = new { reviewThreads = new { nodes = Array.Empty<object>() } } } } }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });

        var sut = new GitHubPullRequestFetcher(
            new GitHubConnectionVerifier(CreateConnectionRepository(clientId, host, ScmProvider.GitHub), httpClientFactory),
            httpClientFactory);

        var result = await sut.FetchAsync(
            "https://github.com",
            "acme",
            "acme/propr",
            42,
            7,
            clientId: clientId,
            cancellationToken: CancellationToken.None);

        Assert.Single(result.ChangedFiles);

        // A collection that fits in one page costs what it always did, down to the request it sends.
        Assert.Equal(["https://api.github.com/repos/acme/propr/pulls/42/files?per_page=100"], fileRequests);
    }

    [Fact]
    public async Task GitHubReviewThreads_SpanMoreThanOnePage_EveryThreadIsReturned()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var httpClientFactory = CreateGitHubHttpClientFactory(async request =>
        {
            if (request.RequestUri!.AbsoluteUri == "https://api.github.com/user")
            {
                return CreateJsonResponse(new { login = "meister-dev" });
            }

            if (request.RequestUri.AbsoluteUri != "https://api.github.com/graphql")
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var body = await request.Content!.ReadAsStringAsync(CancellationToken.None);
            var isSecondPage = body.Contains("\"after\":\"cursor-1\"", StringComparison.Ordinal);

            return CreateJsonResponse(
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
                                    pageInfo = isSecondPage
                                        ? new { hasNextPage = false, endCursor = "cursor-2" }
                                        : new { hasNextPage = true, endCursor = "cursor-1" },
                                    nodes = new object[]
                                    {
                                        BuildThreadNode(
                                            isSecondPage ? "PRRT_602" : "PRRT_601",
                                            isSecondPage ? "src/second.ts" : "src/first.ts",
                                            isSecondPage ? 602 : 601),
                                    },
                                },
                            },
                        },
                    },
                });
        });

        var sut = new GitHubPullRequestFetcher(
            new GitHubConnectionVerifier(CreateConnectionRepository(clientId, host, ScmProvider.GitHub), httpClientFactory),
            httpClientFactory);

        var threads = await sut.FetchThreadsAsync(
            "https://github.com",
            "acme",
            "acme/propr",
            42,
            clientId,
            CancellationToken.None);

        Assert.Equal(["PRRT_601", "PRRT_602"], threads.Select(thread => thread.ThreadId));
    }

    /// <summary>
    ///     Forgejo clamps a requested page size to the host's configured maximum response length, so a read that
    ///     stopped as soon as a page came back smaller than requested would stop on the first page. The total it
    ///     reports is what carries the read past that.
    /// </summary>
    [Fact]
    public async Task ForgejoChangedFiles_HostServesSmallerPagesThanRequested_EveryFileIsInScope()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://codeberg.example.com");
        var httpClientFactory = ForgejoTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://codeberg.example.com/api/v1/user" => ForgejoTestHelpers.CreateJsonResponse(new { login = "meister-dev" }),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42" =>
                    ForgejoTestHelpers.CreateJsonResponse(
                        new
                        {
                            title = "Add assets",
                            body = "Two clamped pages of changed files.",
                            state = "open",
                            merged = false,
                            head = new { @ref = "feature/assets", sha = "head-sha" },
                            @base = new { @ref = "main", sha = "base-sha" },
                        }),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/files?limit=100" => WithTotalCount(
                    ForgejoTestHelpers.CreateJsonResponse(new object[] { new { filename = "assets/first.png", status = "added" } }),
                    2),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/files?page=2&limit=100" =>
                    WithTotalCount(
                        ForgejoTestHelpers.CreateJsonResponse(new object[] { new { filename = "assets/second.png", status = "added" } }),
                        2),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews?limit=100" =>
                    ForgejoTestHelpers.CreateJsonResponse(Array.Empty<object>()),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var sut = new ForgejoPullRequestFetcher(
            new ForgejoConnectionVerifier(
                ForgejoTestHelpers.CreateConnectionRepository(clientId, host),
                httpClientFactory),
            httpClientFactory);

        var result = await sut.FetchAsync(
            "https://codeberg.example.com",
            "acme",
            "acme/propr",
            42,
            7,
            clientId: clientId,
            cancellationToken: CancellationToken.None);

        Assert.Equal(
            ["assets/first.png", "assets/second.png"],
            result.ChangedFiles.Select(file => file.Path));
    }

    [Fact]
    public async Task GitLabDiscussions_SpanMoreThanOnePage_EveryThreadIsReturned()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var httpClientFactory = GitLabTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://gitlab.example.com/api/v4/user" => GitLabTestHelpers.CreateJsonResponse(new { username = "meister-dev" }),

                // GitLab names the next page in a header, and sends it empty on the last one.
                "https://gitlab.example.com/api/v4/projects/101/merge_requests/42/discussions?per_page=100" =>
                    WithNextPage(
                        GitLabTestHelpers.CreateJsonResponse(new object[] { BuildDiscussion("first", 501) }),
                        "2"),
                "https://gitlab.example.com/api/v4/projects/101/merge_requests/42/discussions?per_page=100&page=2" =>
                    WithNextPage(
                        GitLabTestHelpers.CreateJsonResponse(new object[] { BuildDiscussion("second", 502) }),
                        string.Empty),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var sut = new GitLabPullRequestFetcher(
            new GitLabConnectionVerifier(
                GitLabTestHelpers.CreateConnectionRepository(clientId, host),
                httpClientFactory),
            httpClientFactory);

        var threads = await sut.FetchThreadsAsync(
            "https://gitlab.example.com",
            "acme/platform",
            "101",
            42,
            clientId,
            CancellationToken.None);

        Assert.Equal(["first", "second"], threads.Select(thread => thread.ThreadId));
    }

    /// <summary>
    ///     GitLab's change listing is not paginated: past the host's diff limits it drops the remainder and says
    ///     so in an overflow flag. Proceeding on what arrived would be the silent truncation this fixes.
    /// </summary>
    [Fact]
    public async Task GitLabChangeListing_OverflowsTheHostsLimits_FailsRatherThanReviewingPartOfIt()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var httpClientFactory = GitLabTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://gitlab.example.com/api/v4/user" => GitLabTestHelpers.CreateJsonResponse(new { username = "meister-dev" }),
                "https://gitlab.example.com/api/v4/projects/101/merge_requests/42" =>
                    GitLabTestHelpers.CreateJsonResponse(
                        new
                        {
                            title = "An enormous merge request",
                            description = "More diffs than the host will serve.",
                            state = "opened",
                            source_branch = "feature/providers",
                            target_branch = "main",
                            sha = "head-sha",
                            diff_refs = new { base_sha = "base-sha", head_sha = "head-sha", start_sha = "start-sha" },
                            references = new { full = "acme/platform/propr!42", @short = "propr!42" },
                        }),
                "https://gitlab.example.com/api/v4/projects/101/merge_requests/42/changes" =>
                    GitLabTestHelpers.CreateJsonResponse(
                        new
                        {
                            overflow = true,
                            changes = new object[]
                            {
                                new
                                {
                                    old_path = (string?)null, new_path = "src/Fetcher.cs", diff = "+class Fetcher",
                                    new_file = true, deleted_file = false, renamed_file = false,
                                },
                            },
                        }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var sut = new GitLabPullRequestFetcher(
            new GitLabConnectionVerifier(
                GitLabTestHelpers.CreateConnectionRepository(clientId, host),
                httpClientFactory),
            httpClientFactory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.FetchAsync(
            "https://gitlab.example.com",
            "acme/platform",
            "101",
            42,
            7,
            clientId: clientId,
            cancellationToken: CancellationToken.None));

        Assert.Contains("cut short by the host", exception.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be reviewed completely", exception.Message, StringComparison.Ordinal);
    }

    private static object BuildThreadNode(string threadId, string path, int commentId)
    {
        return new
        {
            id = threadId,
            isResolved = false,
            path,
            line = 18,
            comments = new
            {
                nodes = new object[]
                {
                    new
                    {
                        databaseId = commentId,
                        body = "Please handle null.",
                        createdAt = "2026-04-17T10:00:00Z",
                        author = new { login = "octocat", databaseId = 7 },
                    },
                },
            },
        };
    }

    private static object BuildDiscussion(string discussionId, int noteId)
    {
        return new
        {
            id = discussionId,
            individual_note = false,
            notes = new object[]
            {
                new
                {
                    id = noteId,
                    body = "Please handle null.",
                    system = false,
                    resolved = false,
                    created_at = "2026-04-17T10:00:00Z",
                    author = new { id = 99, username = "octocat" },
                    position = new
                    {
                        new_path = "src/Fetcher.cs", old_path = "src/Fetcher.cs", new_line = 18,
                        old_line = (int?)null,
                    },
                },
            },
        };
    }

    private static HttpResponseMessage WithLinkToNextPage(HttpResponseMessage response)
    {
        response.Headers.Add("Link", "<https://api.github.com/next>; rel=\"next\"");
        return response;
    }

    private static HttpResponseMessage WithTotalCount(HttpResponseMessage response, int totalCount)
    {
        response.Headers.Add("X-Total-Count", totalCount.ToString());
        return response;
    }

    private static HttpResponseMessage WithNextPage(HttpResponseMessage response, string nextPage)
    {
        response.Headers.Add("X-Next-Page", nextPage);
        return response;
    }

    private static IClientScmConnectionRepository CreateConnectionRepository(
        Guid clientId,
        ProviderHostRef host,
        ScmProvider provider)
    {
        var repository = Substitute.For<IClientScmConnectionRepository>();
        repository.GetOperationalConnectionAsync(clientId, host, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    Guid.NewGuid(),
                    clientId,
                    provider,
                    host.HostBaseUrl,
                    ScmAuthenticationKind.PersonalAccessToken,
                    provider.ToString(),
                    "provider-token",
                    true));
        return repository;
    }

    private static IHttpClientFactory CreateGitHubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        return CreateGitHubHttpClientFactory(request => Task.FromResult(responder(request)));
    }

    private static IHttpClientFactory CreateGitHubHttpClientFactory(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("GitHubProvider").Returns(new HttpClient(new StubHttpMessageHandler(responder)));
        return factory;
    }

    private static HttpResponseMessage CreateJsonResponse<T>(T payload)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
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

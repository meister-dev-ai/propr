// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.Reviewing;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.Security;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.GitHub;

public sealed class GitHubReviewThreadStatusProviderTests
{
    [Fact]
    public async Task GetReviewerThreadStatusesAsync_ReviewerOwnedThreads_ReturnsHistoryAndReplyCounts()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var connectionRepository = CreateConnectionRepository(clientId, host);
        var httpClientFactory = CreateHttpClientFactory(async request => request.RequestUri!.AbsoluteUri switch
        {
            "https://api.github.com/user" => CreateJsonResponse(new { login = "meister-dev" }),
            "https://api.github.com/repositories/101" => CreateJsonResponse(new { full_name = "acme/propr" }),
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
                                            path = "src/feature.ts",
                                            line = 18,
                                            comments = new
                                            {
                                                nodes = new object[]
                                                {
                                                    new
                                                    {
                                                        databaseId = 501, body = "Please handle null.",
                                                        createdAt = "2026-04-14T08:00:00Z",
                                                        author = new { login = "meister-dev" },
                                                    },
                                                    new
                                                    {
                                                        databaseId = 502, body = "Done.",
                                                        createdAt = "2026-04-14T08:01:00Z",
                                                        author = new { login = "octocat" },
                                                    },
                                                },
                                            },
                                        },
                                        new
                                        {
                                            isResolved = true,
                                            path = "src/ignore.ts",
                                            line = 10,
                                            comments = new
                                            {
                                                nodes = new object[]
                                                {
                                                    new
                                                    {
                                                        databaseId = 601, body = "User thread.",
                                                        createdAt = "2026-04-14T08:02:00Z",
                                                        author = new { login = "octocat" },
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
        var sut = new GitHubReviewThreadStatusProvider(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var result = await sut.GetReviewerThreadStatusesAsync(
            "https://github.com",
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
        Assert.Contains("meister-dev: Please handle null.", entry.CommentHistory);
        Assert.Contains("octocat: Done.", entry.CommentHistory);
    }

    [Fact]
    public async Task GetReviewerThreadStatusesAsync_ResolvedThread_MapsToFixedStatus()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var connectionRepository = CreateConnectionRepository(clientId, host);
        var httpClientFactory = CreateHttpClientFactory(request => request.RequestUri!.AbsoluteUri switch
        {
            "https://api.github.com/user" => CreateJsonResponse(new { login = "meister-dev" }),
            "https://api.github.com/repositories/101" => CreateJsonResponse(new { full_name = "acme/propr" }),
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
                                            isResolved = true,
                                            path = "src/feature.ts",
                                            line = 18,
                                            comments = new
                                            {
                                                nodes = new object[]
                                                {
                                                    new
                                                    {
                                                        databaseId = 501, body = "Please handle null.",
                                                        createdAt = "2026-04-14T08:00:00Z",
                                                        author = new { login = "meister-dev" },
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
        var sut = new GitHubReviewThreadStatusProvider(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var result = await sut.GetReviewerThreadStatusesAsync(
            "https://github.com",
            "acme",
            "101",
            42,
            Guid.Empty,
            clientId,
            CancellationToken.None);

        var entry = Assert.Single(result);
        Assert.Equal("Fixed", entry.Status);
    }

    [Fact]
    public async Task GetReviewerThreadStatusesAsync_AcceptsOwnerRepoRepositoryIdWithoutNumericLookup()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var connectionRepository = CreateConnectionRepository(clientId, host);
        var httpClientFactory = CreateHttpClientFactory(request => request.RequestUri!.AbsoluteUri switch
        {
            "https://api.github.com/user" => CreateJsonResponse(new { login = "meister-dev" }),
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
                                            path = "src/feature.ts",
                                            line = 18,
                                            comments = new
                                            {
                                                nodes = new object[]
                                                {
                                                    new
                                                    {
                                                        databaseId = 501, body = "Please handle null.",
                                                        createdAt = "2026-04-14T08:00:00Z",
                                                        author = new { login = "meister-dev" },
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
        var sut = new GitHubReviewThreadStatusProvider(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var result = await sut.GetReviewerThreadStatusesAsync(
            "https://github.com",
            "meister-dev-ai",
            "meister-dev-ai/propr",
            8,
            Guid.Empty,
            clientId,
            CancellationToken.None);

        var entry = Assert.Single(result);
        Assert.Equal(501, entry.ThreadId);
    }

    [Fact]
    public async Task GetReviewerThreadStatusesAsync_AllowsNullGraphQlCommentDatabaseIds()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var connectionRepository = CreateConnectionRepository(clientId, host);
        var httpClientFactory = CreateHttpClientFactory(request => request.RequestUri!.AbsoluteUri switch
        {
            "https://api.github.com/user" => CreateJsonResponse(new { login = "meister-dev" }),
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
                                            path = "src/feature.ts",
                                            line = 18,
                                            comments = new
                                            {
                                                nodes = new object[]
                                                {
                                                    new
                                                    {
                                                        databaseId = (int?)null,
                                                        body = "Please handle null.",
                                                        createdAt = "2026-04-14T08:00:00Z",
                                                        author = new { login = "meister-dev" },
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
        var sut = new GitHubReviewThreadStatusProvider(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var result = await sut.GetReviewerThreadStatusesAsync(
            "https://github.com",
            "meister-dev-ai",
            "meister-dev-ai/propr",
            8,
            Guid.Empty,
            clientId,
            CancellationToken.None);

        var entry = Assert.Single(result);
        Assert.Equal(0, entry.ThreadId);
    }

    [Fact]
    public async Task GetReviewerThreadStatusesAsync_AllowsLargeGraphQlCommentDatabaseIds()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var connectionRepository = CreateConnectionRepository(clientId, host);
        var httpClientFactory = CreateHttpClientFactory(request => request.RequestUri!.AbsoluteUri switch
        {
            "https://api.github.com/user" => CreateJsonResponse(new { login = "meister-dev" }),
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
                                            path = "src/feature.ts",
                                            line = 18,
                                            comments = new
                                            {
                                                nodes = new object[]
                                                {
                                                    new
                                                    {
                                                        databaseId = 3197004556L,
                                                        body = "Please handle null.",
                                                        createdAt = "2026-04-14T08:00:00Z",
                                                        author = new { login = "meister-dev" },
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
        var sut = new GitHubReviewThreadStatusProvider(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var result = await sut.GetReviewerThreadStatusesAsync(
            "https://github.com",
            "meister-dev-ai",
            "meister-dev-ai/propr",
            8,
            Guid.Empty,
            clientId,
            CancellationToken.None);

        var entry = Assert.Single(result);
        Assert.Equal(3197004556L, entry.ThreadId);
    }

    [Fact]
    public async Task GetReviewerThreadStatusesAsync_AppInstallation_UsesInstallationTokenForGraphQlLookup()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var connectionRepository = GitHubAppTestHelpers.CreateAppInstallationConnectionRepository(clientId, host);
        string? graphQlAuthorization = null;
        var httpClientFactory = CreateHttpClientFactory(request => request.RequestUri!.AbsoluteUri switch
        {
            "https://api.github.com/app/installations/789012" => CreateJsonResponse(
                new { account = new { login = "acme-platform" }, app_slug = "propr-review" }),
            "https://api.github.com/app/installations/789012/access_tokens" => CreateJsonResponse(
                new
                {
                    token = "installation-token",
                    expires_at = DateTimeOffset.UtcNow.AddHours(1),
                }),
            "https://api.github.com/graphql" => CaptureGraphQl(request),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var sut = new GitHubReviewThreadStatusProvider(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var result = await sut.GetReviewerThreadStatusesAsync(
            "https://github.com",
            "acme",
            "acme/propr",
            42,
            Guid.Empty,
            clientId,
            CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("installation-token", graphQlAuthorization);
        return;

        HttpResponseMessage CaptureGraphQl(HttpRequestMessage request)
        {
            graphQlAuthorization = request.Headers.Authorization?.Parameter;
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
                                    nodes = new object[]
                                    {
                                        new
                                        {
                                            isResolved = false,
                                            path = "src/feature.ts",
                                            line = 18,
                                            comments = new
                                            {
                                                nodes = new object[]
                                                {
                                                    new
                                                    {
                                                        databaseId = 501,
                                                        body = "Please handle null.",
                                                        createdAt = "2026-04-14T08:00:00Z",
                                                        author = new { login = "propr-review[bot]" },
                                                    },
                                                },
                                            },
                                        },
                                    },
                                },
                            },
                        },
                    },
                });
        }
    }

    [Fact]
    public async Task GetReviewerThreadStatusesAsync_OutdatedThread_ReportsUnknownRatherThanChanged()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var connectionRepository = CreateConnectionRepository(clientId, host);
        var httpClientFactory = CreateHttpClientFactory(request => request.RequestUri!.AbsoluteUri switch
        {
            "https://api.github.com/user" => CreateJsonResponse(new { login = "meister-dev" }),
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
                                            isResolved = true,
                                            isOutdated = true,
                                            path = "src/changed.ts",
                                            line = 18,
                                            comments = new
                                            {
                                                nodes = new object[]
                                                {
                                                    new
                                                    {
                                                        databaseId = 501, body = "Fix this.",
                                                        createdAt = "2026-04-14T08:00:00Z",
                                                        author = new { login = "meister-dev" },
                                                    },
                                                },
                                            },
                                        },
                                        new
                                        {
                                            isResolved = true,
                                            isOutdated = false,
                                            path = "src/untouched.ts",
                                            line = 10,
                                            comments = new
                                            {
                                                nodes = new object[]
                                                {
                                                    new
                                                    {
                                                        databaseId = 601, body = "Fix this too.",
                                                        createdAt = "2026-04-14T08:02:00Z",
                                                        author = new { login = "meister-dev" },
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
        var sut = new GitHubReviewThreadStatusProvider(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var result = await sut.GetReviewerThreadStatusesAsync(
            "https://github.com",
            "meister-dev-ai",
            "meister-dev-ai/propr",
            8,
            Guid.Empty,
            clientId,
            CancellationToken.None);

        Assert.Equal(2, result.Count);

        // An outdated thread is undetermined, not a corroborated code change: isOutdated is too weak a
        // signal to trust a claimed fix as grounded (it fires on rebases/unrelated churn too).
        Assert.Equal(
            ThreadAnchorCodeChange.Unknown,
            result.Single(entry => entry.ThreadId == 501).CodeChangedSinceRaised);

        // A thread that is still current genuinely has an unchanged anchor.
        Assert.Equal(
            ThreadAnchorCodeChange.Unchanged,
            result.Single(entry => entry.ThreadId == 601).CodeChangedSinceRaised);
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
            .Returns(new HttpClient(new StubHttpMessageHandler(request => Task.FromResult(responder(request)))));
        return factory;
    }

    private static IHttpClientFactory CreateHttpClientFactory(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
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

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

public sealed class GitHubReviewAssignmentProviderTests
{
    [Fact]
    public async Task RequestReviewerAsync_WhenReviewerNotYetRequested_PostsReviewerRequest()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "4201", 42);
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot", "Meister Review Bot", true);
        var connectionRepository = CreateConnectionRepository(clientId, host);

        string? postedBody = null;
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("GitHubProvider")
            .Returns(
                new HttpClient(
                    new StubHttpMessageHandler(async request =>
                    {
                        if (request.RequestUri!.AbsoluteUri == "https://api.github.com/user")
                        {
                            return CreateJsonResponse(new { login = "meister-dev" });
                        }

                        if (request.Method == HttpMethod.Get && request.RequestUri.AbsoluteUri ==
                            "https://api.github.com/repos/acme/propr/pulls/42/requested_reviewers")
                        {
                            return CreateJsonResponse(
                                new
                                {
                                    users = new object[]
                                    {
                                        new { login = "other-reviewer" },
                                    },
                                });
                        }

                        if (request.Method == HttpMethod.Post && request.RequestUri.AbsoluteUri ==
                            "https://api.github.com/repos/acme/propr/pulls/42/requested_reviewers")
                        {
                            postedBody = await request.Content!.ReadAsStringAsync();
                            return CreateJsonResponse(new { ok = true });
                        }

                        return new HttpResponseMessage(HttpStatusCode.NotFound);
                    })));

        var sut = new GitHubReviewAssignmentProvider(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        await sut.RequestReviewerAsync(clientId, review, reviewer);

        Assert.NotNull(postedBody);
        Assert.Contains("meister-review-bot", postedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestReviewerAsync_WhenReviewerAlreadyRequested_SkipsPost()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "4201", 42);
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot", "Meister Review Bot", true);
        var connectionRepository = CreateConnectionRepository(clientId, host);

        var postInvoked = false;
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("GitHubProvider")
            .Returns(new HttpClient(new StubHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))));
        httpClientFactory.CreateClient("GitHubProvider")
            .Returns(
                new HttpClient(
                    new StubHttpMessageHandler(async request =>
                    {
                        if (request.RequestUri!.AbsoluteUri == "https://api.github.com/user")
                        {
                            return CreateJsonResponse(new { login = "meister-dev" });
                        }

                        if (request.Method == HttpMethod.Get && request.RequestUri.AbsoluteUri ==
                            "https://api.github.com/repos/acme/propr/pulls/42/requested_reviewers")
                        {
                            return CreateJsonResponse(
                                new
                                {
                                    users = new object[]
                                    {
                                        new { login = "meister-review-bot" },
                                    },
                                });
                        }

                        if (request.Method == HttpMethod.Post)
                        {
                            postInvoked = true;
                        }

                        return new HttpResponseMessage(HttpStatusCode.NotFound);
                    })));

        var sut = new GitHubReviewAssignmentProvider(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        await sut.RequestReviewerAsync(clientId, review, reviewer);

        Assert.False(postInvoked);
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

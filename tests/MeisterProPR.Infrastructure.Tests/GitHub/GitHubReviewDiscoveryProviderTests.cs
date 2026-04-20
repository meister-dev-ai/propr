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

public sealed class GitHubReviewDiscoveryProviderTests
{
    [Fact]
    public async Task ListOpenReviewsAsync_WithRequestedReviewer_FiltersToMatchingReviews()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot", "Meister Review Bot", true);
        var connectionRepository = CreateConnectionRepository(clientId, host);
        var httpClientFactory = CreateHttpClientFactory(request => request.RequestUri!.AbsoluteUri switch
        {
            "https://api.github.com/user" => CreateJsonResponse(new { login = "meister-dev" }),
            "https://api.github.com/repos/acme/propr/pulls?state=open&per_page=100" => CreateJsonResponse(
                new object[]
                {
                    new
                    {
                        id = 4201,
                        number = 42,
                        title = "Provider neutral adapters",
                        html_url = "https://github.com/acme/propr/pull/42",
                        state = "open",
                        merged_at = (string?)null,
                        head = new { @ref = "feature/providers", sha = "head-sha" },
                        @base = new { @ref = "main", sha = "base-sha" },
                        requested_reviewers = new object[]
                        {
                            new { id = 99, login = "meister-review-bot", name = "Meister Review Bot", type = "Bot" },
                        },
                    },
                    new
                    {
                        id = 4301,
                        number = 43,
                        title = "Unassigned review",
                        html_url = "https://github.com/acme/propr/pull/43",
                        state = "open",
                        merged_at = (string?)null,
                        head = new { @ref = "feature/other", sha = "head-sha-2" },
                        @base = new { @ref = "main", sha = "base-sha-2" },
                        requested_reviewers = new object[]
                        {
                            new { id = 12, login = "other-reviewer", name = "Other Reviewer", type = "User" },
                        },
                    },
                }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var sut = new GitHubReviewDiscoveryProvider(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var result = await sut.ListOpenReviewsAsync(clientId, repository, reviewer);

        var item = Assert.Single(result);
        Assert.Equal(42, item.CodeReview.Number);
        Assert.Equal(CodeReviewState.Open, item.ReviewState);
        Assert.Equal("head-sha", item.ReviewRevision!.HeadSha);
        Assert.Equal("base-sha", item.ReviewRevision.BaseSha);
        Assert.Equal("meister-review-bot", item.RequestedReviewerIdentity!.Login);
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

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.Identity;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.Security;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.GitHub;

public sealed class GitHubReviewerIdentityServiceTests
{
    [Fact]
    public async Task ResolveCandidatesAsync_ReturnsSortedReviewerIdentities()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
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

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("GitHubProvider")
            .Returns(
                new HttpClient(
                    new StubHttpMessageHandler(request => request.RequestUri!.AbsoluteUri switch
                    {
                        "https://api.github.com/user" => CreateJsonResponse(new { login = "meister-dev" }),
                        "https://api.github.com/search/users?q=meister%20in%3Alogin&per_page=20&type=Users" =>
                            CreateJsonResponse(
                                new
                                {
                                    items = new object[]
                                    {
                                        new { id = 2, login = "meister-review-bot[bot]", type = "Bot" },
                                        new { id = 1, login = "meister-dev", type = "User" },
                                    },
                                }),
                        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                    })));

        var sut = new GitHubReviewerIdentityService(
            new GitHubConnectionVerifier(repository, httpClientFactory),
            httpClientFactory);

        var candidates = await sut.ResolveCandidatesAsync(clientId, host, "meister");

        Assert.Equal(2, candidates.Count);
        Assert.Equal("meister-dev", candidates[0].Login);
        Assert.False(candidates[0].IsBot);
        Assert.True(candidates[1].IsBot);
    }

    [Fact]
    public async Task ResolveCandidatesAsync_AppInstallation_ReturnsCollaboratorsFromInstalledRepositories()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = GitHubAppTestHelpers.CreateAppInstallationConnectionRepository(clientId, host);

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("GitHubProvider")
            .Returns(
                new HttpClient(
                    new StubHttpMessageHandler(request => request.RequestUri!.AbsoluteUri switch
                    {
                        "https://api.github.com/app/installations/789012" => CreateJsonResponse(new { account = new { login = "acme-platform" } }),
                        "https://api.github.com/app/installations/789012/access_tokens" => CreateAccessTokenResponse(),
                        "https://api.github.com/installation/repositories?per_page=100&page=1" => CreateJsonResponse(
                            new
                            {
                                repositories = new object[]
                                {
                                    new { full_name = "acme/propr" },
                                    new { full_name = "acme/platform" },
                                },
                            }),
                        "https://api.github.com/repos/acme/propr/collaborators?per_page=100" => CreateJsonResponse(
                            new object[]
                            {
                                new { id = 2, login = "meister-review-bot[bot]", type = "Bot" },
                                new { id = 1, login = "meister-dev", type = "User" },
                            }),
                        "https://api.github.com/repos/acme/platform/collaborators?per_page=100" => CreateJsonResponse(
                            new object[]
                            {
                                new { id = 1, login = "meister-dev", type = "User" },
                            }),
                        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                    })));

        var sut = new GitHubReviewerIdentityService(
            new GitHubConnectionVerifier(repository, httpClientFactory),
            httpClientFactory);

        var candidates = await sut.ResolveCandidatesAsync(clientId, host, "meister");

        Assert.Equal(2, candidates.Count);
        Assert.Equal("meister-dev", candidates[0].Login);
        Assert.Equal("meister-review-bot[bot]", candidates[1].Login);
        Assert.True(candidates[1].IsBot);
    }

    private static HttpResponseMessage CreateJsonResponse<T>(T payload)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload)),
        };
    }

    private static HttpResponseMessage CreateAccessTokenResponse()
    {
        return CreateJsonResponse(
            new
            {
                token = "installation-token",
                expires_at = DateTimeOffset.UtcNow.AddHours(1),
            });
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

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.Discovery;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.Security;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.GitHub;

public sealed class GitHubDiscoveryServiceTests
{
    [Fact]
    public async Task ListScopesAsync_IncludesAuthenticatedUserAndOrganizations()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var connectionRepository = CreateConnectionRepository(clientId, host);
        var httpClientFactory = CreateHttpClientFactory(request => request.RequestUri!.AbsoluteUri switch
        {
            "https://api.github.com/user" => CreateJsonResponse(new { login = "meister-dev" }),
            "https://api.github.com/user/orgs?per_page=100" => CreateJsonResponse(
                new[]
                {
                    new { login = "acme" },
                    new { login = "platform" },
                }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var sut = new GitHubDiscoveryService(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var scopes = await sut.ListScopesAsync(clientId, host);

        Assert.Equal(["acme", "meister-dev", "platform"], scopes);
    }

    [Fact]
    public async Task ListRepositoriesAsync_PersonalScope_ReturnsNormalizedRepositoryReferences()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var connectionRepository = CreateConnectionRepository(clientId, host);
        var httpClientFactory = CreateHttpClientFactory(request => request.RequestUri!.AbsoluteUri switch
        {
            "https://api.github.com/user" => CreateJsonResponse(new { login = "meister-dev" }),
            "https://api.github.com/user/repos?per_page=100&affiliation=owner,collaborator,organization_member" =>
                CreateJsonResponse(
                    new[]
                    {
                        new { id = 101, full_name = "meister-dev/propr", owner = new { login = "meister-dev" } },
                        new { id = 102, full_name = "meister-dev/another", owner = new { login = "meister-dev" } },
                    }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var sut = new GitHubDiscoveryService(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var repositories = await sut.ListRepositoriesAsync(clientId, host, "meister-dev");

        Assert.Equal(2, repositories.Count);
        Assert.Equal("101", repositories[0].ExternalRepositoryId);
        Assert.Equal("meister-dev/propr", repositories[0].ProjectPath);
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

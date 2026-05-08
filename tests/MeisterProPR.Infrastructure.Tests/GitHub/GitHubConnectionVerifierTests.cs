// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.Security;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.GitHub;

public sealed class GitHubConnectionVerifierTests
{
    [Fact]
    public async Task VerifyAsync_ValidPersonalAccessToken_ReturnsAuthenticatedLogin()
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
                    new StubHttpMessageHandler(request =>
                        request.RequestUri!.AbsoluteUri == "https://api.github.com/user"
                            ? CreateJsonResponse(new { login = "meister-dev-bot" })
                            : new HttpResponseMessage(HttpStatusCode.NotFound))));

        var sut = new GitHubConnectionVerifier(repository, httpClientFactory);

        var result = await sut.VerifyAsync(clientId, host);

        Assert.Equal("meister-dev-bot", result.AuthenticatedLogin);
        Assert.Equal("meister-dev-bot", result.AuthenticatedActorLogin);
        Assert.Equal("ghp_test", result.Connection.Secret);
    }

    [Fact]
    public async Task VerifyAsync_InvalidToken_ThrowsInvalidOperationException()
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
            .Returns(new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized))));

        var sut = new GitHubConnectionVerifier(repository, httpClientFactory);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.VerifyAsync(clientId, host));

        Assert.Contains("authentication failed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_ValidAppInstallation_ReturnsInstallationAccountLogin()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = GitHubAppTestHelpers.CreateAppInstallationConnectionRepository(clientId, host);

        var installationTokenRequests = 0;
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("GitHubProvider")
            .Returns(
                new HttpClient(
                    new StubHttpMessageHandler(request => request.RequestUri!.AbsoluteUri switch
                    {
                        "https://api.github.com/app/installations/789012" => CreateJsonResponse(
                            new { account = new { login = "acme-platform" } }),
                        "https://api.github.com/app/installations/789012/access_tokens" =>
                            CreateAccessTokenResponse(++installationTokenRequests),
                        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                    })));

        var sut = new GitHubConnectionVerifier(repository, httpClientFactory);

        var result = await sut.VerifyAsync(clientId, host);

        Assert.Equal("acme-platform", result.AuthenticatedLogin);
        Assert.Equal("acme-platform", result.AuthenticatedActorLogin);
        Assert.Equal(ScmAuthenticationKind.AppInstallation, result.Connection.AuthenticationKind);
        Assert.Equal("installation-token-1", await result.GetAccessTokenAsync());
    }

    [Fact]
    public async Task VerifyAsync_AppInstallationWithInvalidPrivateKey_ThrowsInvalidOperationException()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = GitHubAppTestHelpers.CreateAppInstallationConnectionRepository(
            clientId,
            host,
            privateKeyPem: "not-a-private-key");

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("GitHubProvider")
            .Returns(new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))));

        var sut = new GitHubConnectionVerifier(repository, httpClientFactory);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.VerifyAsync(clientId, host));

        Assert.Contains("private key is invalid", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_AppInstallationNotFound_ThrowsInvalidOperationException()
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
                        "https://api.github.com/app/installations/789012" => new HttpResponseMessage(HttpStatusCode.NotFound),
                        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                    })));

        var sut = new GitHubConnectionVerifier(repository, httpClientFactory);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.VerifyAsync(clientId, host));

        Assert.Contains("installation was not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_AppInstallationRefreshesExpiredCachedToken()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = GitHubAppTestHelpers.CreateAppInstallationConnectionRepository(clientId, host);
        var tokenRequests = 0;
        var httpClientFactory = CreateHttpClientFactory(request => request.RequestUri!.AbsoluteUri switch
        {
            "https://api.github.com/app/installations/789012" => CreateJsonResponse(
                new { account = new { login = "acme-platform" } }),
            "https://api.github.com/app/installations/789012/access_tokens" => CreateJsonResponse(
                new
                {
                    token = $"installation-token-{++tokenRequests}",
                    expires_at = tokenRequests == 1
                        ? DateTimeOffset.UtcNow.AddMinutes(4)
                        : DateTimeOffset.UtcNow.AddHours(1),
                }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var authenticationService = new GitHubAuthenticationService(httpClientFactory);

        var sut = new GitHubConnectionVerifier(
            repository,
            httpClientFactory,
            authenticationService);

        var result = await sut.VerifyAsync(clientId, host);
        var firstToken = await result.GetAccessTokenAsync();
        var secondToken = await result.GetAccessTokenAsync();

        Assert.Equal("installation-token-2", firstToken);
        Assert.Equal("installation-token-2", secondToken);
        Assert.Equal(2, tokenRequests);
    }

    private static HttpResponseMessage CreateJsonResponse<T>(T payload)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload)),
        };
    }

    private static HttpResponseMessage CreateAccessTokenResponse(int requestNumber)
    {
        return CreateJsonResponse(
            new
            {
                token = $"installation-token-{requestNumber}",
                expires_at = DateTimeOffset.UtcNow.AddHours(1),
            });
    }

    private static IHttpClientFactory CreateHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("GitHubProvider").Returns(new HttpClient(new StubHttpMessageHandler(responder)));
        return factory;
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

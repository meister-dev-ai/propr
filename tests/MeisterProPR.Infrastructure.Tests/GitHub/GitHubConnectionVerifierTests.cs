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

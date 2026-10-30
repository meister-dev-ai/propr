// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;
using MeisterDev.ProPR.Infrastructure.Tests.GitHub;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers.GitHub.Reviewing;

public sealed class GitHubReviewWorkspaceRemoteResolverTests
{
    [Fact]
    public async Task ResolveAsync_PersonalAccessToken_ReturnsPullRefAndBasicCredentials()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        connectionRepository.GetOperationalConnectionAsync(clientId, host, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    Guid.NewGuid(), clientId, ScmProvider.GitHub, host.HostBaseUrl, ScmAuthenticationKind.PersonalAccessToken, null, null, "GitHub",
                    "token-value", true));
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("GitHubProvider").Returns(
            new HttpClient(
                new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { login = "acme" }),
                })));
        var verifier = new GitHubConnectionVerifier(connectionRepository, httpClientFactory);

        var sut = new GitHubReviewWorkspaceRemoteResolver(verifier);
        var result = await sut.ResolveAsync(CreateRequest(host, clientId), CancellationToken.None);

        Assert.Contains("refs/pull/42/head", string.Join(' ', result.FetchRefSpecs), StringComparison.Ordinal);
        Assert.Equal("x-access-token:token-value", DecodeBasicCredentials(result.AuthorizationHeader));
    }

    [Fact]
    public async Task ResolveAsync_AppInstallation_SendsInstallationTokenAsBasicCredentials()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var connectionRepository = GitHubAppTestHelpers.CreateAppInstallationConnectionRepository(clientId, host);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("GitHubProvider").Returns(
            new HttpClient(
                new StubHandler(request => request.RequestUri!.AbsoluteUri switch
                {
                    "https://api.github.com/app/installations/789012/access_tokens" => new HttpResponseMessage(HttpStatusCode.Created)
                    {
                        Content = JsonContent.Create(new { token = "installation-token", expires_at = DateTimeOffset.UtcNow.AddHours(1) }),
                    },
                    _ => new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new { account = new { login = "acme" } }),
                    },
                })));
        var verifier = new GitHubConnectionVerifier(connectionRepository, httpClientFactory);

        var sut = new GitHubReviewWorkspaceRemoteResolver(verifier);
        var result = await sut.ResolveAsync(CreateRequest(host, clientId), CancellationToken.None);

        Assert.Equal("x-access-token:installation-token", DecodeBasicCredentials(result.AuthorizationHeader));
    }

    private static string DecodeBasicCredentials(string? authorizationHeader)
    {
        const string prefix = "AUTHORIZATION: Basic ";
        Assert.NotNull(authorizationHeader);
        Assert.StartsWith(prefix, authorizationHeader, StringComparison.Ordinal);
        return Encoding.UTF8.GetString(Convert.FromBase64String(authorizationHeader[prefix.Length..]));
    }

    private static ReviewRepositoryWorkspaceRequest CreateRequest(ProviderHostRef host, Guid clientId)
    {
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        return new ReviewRepositoryWorkspaceRequest(
            Guid.NewGuid(), clientId, ScmProvider.GitHub, host.HostBaseUrl, repository, 42, new ReviewRevision("head", "base", null, null, null),
            "feature/demo", "main");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }
}

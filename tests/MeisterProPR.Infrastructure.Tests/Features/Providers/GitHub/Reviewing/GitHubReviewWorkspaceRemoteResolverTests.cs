// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.Reviewing;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.Security;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.Features.Providers.GitHub.Reviewing;

public sealed class GitHubReviewWorkspaceRemoteResolverTests
{
    [Fact]
    public async Task ResolveAsync_ReturnsPullRefAndAuthorizationHeader()
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
        Assert.StartsWith("AUTHORIZATION: Bearer ", result.AuthorizationHeader, StringComparison.Ordinal);
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

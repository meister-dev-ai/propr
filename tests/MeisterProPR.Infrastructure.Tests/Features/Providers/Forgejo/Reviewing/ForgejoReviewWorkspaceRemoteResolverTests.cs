// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.Forgejo.Reviewing;
using MeisterProPR.Infrastructure.Features.Providers.Forgejo.Security;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.Features.Providers.Forgejo.Reviewing;

public sealed class ForgejoReviewWorkspaceRemoteResolverTests
{
    [Fact]
    public async Task ResolveAsync_ReturnsPullRefAndTokenHeader()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://codeberg.example.com");
        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        connectionRepository.GetOperationalConnectionAsync(clientId, host, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    Guid.NewGuid(), clientId, ScmProvider.Forgejo, host.HostBaseUrl, ScmAuthenticationKind.PersonalAccessToken, null, null, "Forgejo", "token",
                    true));
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("ForgejoProvider").Returns(
            new HttpClient(
                new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { login = "user" }),
                })));
        var verifier = new ForgejoConnectionVerifier(connectionRepository, httpClientFactory);

        var sut = new ForgejoReviewWorkspaceRemoteResolver(verifier);
        var result = await sut.ResolveAsync(CreateRequest(host, clientId), CancellationToken.None);

        Assert.Contains("refs/pull/42/head", string.Join(' ', result.FetchRefSpecs), StringComparison.Ordinal);
        Assert.Equal("AUTHORIZATION: token token", result.AuthorizationHeader);
    }

    private static ReviewRepositoryWorkspaceRequest CreateRequest(ProviderHostRef host, Guid clientId)
    {
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        return new ReviewRepositoryWorkspaceRequest(
            Guid.NewGuid(), clientId, ScmProvider.Forgejo, host.HostBaseUrl, repository, 42, new ReviewRevision("head", "base", null, null, null),
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

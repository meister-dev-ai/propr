// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.GitLab.Reviewing;
using MeisterProPR.Infrastructure.Features.Providers.GitLab.Security;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.Features.Providers.GitLab.Reviewing;

public sealed class GitLabReviewWorkspaceRemoteResolverTests
{
    [Fact]
    public async Task ResolveAsync_ReturnsMergeRequestRefAndPrivateTokenHeader()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        connectionRepository.GetOperationalConnectionAsync(clientId, host, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    Guid.NewGuid(), clientId, ScmProvider.GitLab, host.HostBaseUrl, ScmAuthenticationKind.PersonalAccessToken, null, null, "GitLab", "token",
                    true));
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("GitLabProvider").Returns(
            new HttpClient(
                new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { username = "user" }),
                })));
        var verifier = new GitLabConnectionVerifier(connectionRepository, httpClientFactory);

        var sut = new GitLabReviewWorkspaceRemoteResolver(verifier);
        var result = await sut.ResolveAsync(CreateRequest(host, clientId), CancellationToken.None);

        Assert.Contains("refs/merge-requests/42/head", string.Join(' ', result.FetchRefSpecs), StringComparison.Ordinal);
        Assert.Equal("PRIVATE-TOKEN: token", result.AuthorizationHeader);
    }

    private static ReviewRepositoryWorkspaceRequest CreateRequest(ProviderHostRef host, Guid clientId)
    {
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        return new ReviewRepositoryWorkspaceRequest(
            Guid.NewGuid(), clientId, ScmProvider.GitLab, host.HostBaseUrl, repository, 42, new ReviewRevision("head", "base", null, null, null),
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

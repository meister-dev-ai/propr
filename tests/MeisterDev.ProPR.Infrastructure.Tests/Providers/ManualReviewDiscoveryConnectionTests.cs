// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Security;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Security;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Providers;

public sealed class ManualReviewDiscoveryConnectionTests
{
    [Theory]
    [InlineData(ScmProvider.GitHub)]
    [InlineData(ScmProvider.GitLab)]
    [InlineData(ScmProvider.Forgejo)]
    public async Task ExplicitContext_UsesSelectedCredentialWithoutHostLookup(ScmProvider provider)
    {
        var clientId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var host = new ProviderHostRef(provider, "https://scm.example.test");
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetOperationalConnectionByIdAsync(clientId, connectionId, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    connectionId, clientId, provider, host.HostBaseUrl,
                    ScmAuthenticationKind.PersonalAccessToken, "Selected", "selected-fixture", true));
        var handler = new Handler(provider);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        var result = await CreateProvider(provider, connections, factory).ListOpenReviewsAsync(
            clientId,
            new RepositoryRef(host, "101", "team", "team/repo"), null, context: new(connectionId, host.HostBaseUrl));

        Assert.Empty(result);
        Assert.Equal(2, handler.RequestCount);
        await connections.DidNotReceiveWithAnyArgs().GetOperationalConnectionAsync(default, default!, default);
        await connections.Received(1).GetOperationalConnectionByIdAsync(clientId, connectionId, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ScmProvider.GitHub)]
    [InlineData(ScmProvider.GitLab)]
    [InlineData(ScmProvider.Forgejo)]
    public async Task ExplicitContext_MissingSelectedCredentialDoesNotUseAlternative(ScmProvider provider)
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(provider, "https://scm.example.test");
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetOperationalConnectionAsync(clientId, host, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    Guid.NewGuid(), clientId, provider, host.HostBaseUrl,
                    ScmAuthenticationKind.PersonalAccessToken, "Alternative", "alternative-fixture", true));
        var handler = new Handler(provider);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateProvider(provider, connections, factory)
            .ListOpenReviewsAsync(
                clientId, new RepositoryRef(host, "101", "team", "team/repo"), null,
                context: new(Guid.NewGuid(), host.HostBaseUrl)));

        Assert.Equal(0, handler.RequestCount);
        await connections.DidNotReceiveWithAnyArgs().GetOperationalConnectionAsync(default, default!, default);
    }

    private static IReviewDiscoveryProvider CreateProvider(
        ScmProvider provider,
        IClientScmConnectionRepository connections, IHttpClientFactory factory) => provider switch
    {
        ScmProvider.GitHub => new GitHubReviewDiscoveryProvider(new GitHubConnectionVerifier(connections, factory), factory),
        ScmProvider.GitLab => new GitLabReviewDiscoveryProvider(new GitLabConnectionVerifier(connections, factory), factory),
        ScmProvider.Forgejo => new ForgejoReviewDiscoveryProvider(new ForgejoConnectionVerifier(connections, factory), factory),
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    [Theory]
    [InlineData(ScmProvider.GitHub, "client")]
    [InlineData(ScmProvider.GitLab, "provider")]
    [InlineData(ScmProvider.Forgejo, "host")]
    [InlineData(ScmProvider.GitHub, "inactive")]
    [InlineData(ScmProvider.GitLab, "id")]
    [InlineData(ScmProvider.Forgejo, "pathCase")]
    [InlineData(ScmProvider.Forgejo, "path")]
    public async Task ExplicitContext_RejectsInvalidReturnedCredentialsBeforeProviderAccess(ScmProvider provider, string mismatch)
    {
        var clientId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var host = new ProviderHostRef(provider, "https://scm.example.test");
        var connections = Substitute.For<IClientScmConnectionRepository>();
        var credential = new ClientScmConnectionCredentialDto(
            connectionId, clientId, provider, host.HostBaseUrl,
            ScmAuthenticationKind.PersonalAccessToken, "Selected", "selected-fixture", true);
        credential = mismatch switch
        {
            "client" => credential with { ClientId = Guid.NewGuid() },
            "provider" => credential with { ProviderFamily = ScmProvider.Forgejo },
            "host" => credential with { HostBaseUrl = "https://other.example.test" },
            "inactive" => credential with { IsActive = false },
            "id" => credential with { Id = Guid.NewGuid() },
            "pathCase" => credential with { HostBaseUrl = host.HostBaseUrl + "/Scope" },
            "path" => credential with { HostBaseUrl = host.HostBaseUrl + "/installed" },
            _ => credential,
        };
        connections.GetOperationalConnectionByIdAsync(clientId, connectionId, Arg.Any<CancellationToken>()).Returns(credential);
        var handler = new Handler(provider);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateProvider(provider, connections, factory)
            .ListOpenReviewsAsync(
                clientId, new RepositoryRef(host, "101", "team", "team/repo"), null,
                context: new(connectionId, host.HostBaseUrl + (mismatch == "pathCase" ? "/scope" : ""))));
        Assert.Equal(0, handler.RequestCount);
    }

    private sealed class Handler(ScmProvider provider) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.RequestCount++;
            if (provider == ScmProvider.GitLab)
            {
                Assert.Equal("selected-fixture", Assert.Single(request.Headers.GetValues("PRIVATE-TOKEN")));
            }
            else
            {
                Assert.Equal("selected-fixture", request.Headers.Authorization?.Parameter);
            }

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = request.RequestUri!.AbsolutePath.EndsWith("/user", StringComparison.Ordinal)
                        ? JsonContent.Create(new { login = "fixture", username = "fixture" })
                        : JsonContent.Create(Array.Empty<object>()),
                });
        }
    }
}

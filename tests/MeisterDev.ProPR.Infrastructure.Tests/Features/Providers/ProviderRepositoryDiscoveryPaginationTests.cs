// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Discovery;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Security;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Discovery;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Discovery;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Security;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers;

/// <summary>
///     The listings the guided pickers are built from, proven past one page. A truncated list reads as an
///     owner having only those repositories, so an operator would never learn the rest exist.
/// </summary>
public sealed class ProviderRepositoryDiscoveryPaginationTests
{
    private static readonly Guid ClientId = Guid.NewGuid();

    [Fact]
    public async Task GitHubOwners_SpanMoreThanOnePage_EveryOwnerIsOffered()
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var factory = CreateFactory(
            "GitHubProvider", request => request.RequestUri!.AbsoluteUri switch
            {
                "https://api.github.com/user" => Json(new { login = "meister-dev" }),
                "https://api.github.com/user/orgs?per_page=100&page=1" => WithGitHubNextPage(Json(new object[] { new { login = "acme" } })),
                "https://api.github.com/user/orgs?per_page=100&page=2" => Json(new object[] { new { login = "contoso" } }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var sut = new GitHubDiscoveryService(
            new GitHubConnectionVerifier(Connections(ScmProvider.GitHub, host), factory),
            factory);

        var scopes = await sut.ListScopesAsync(ClientId, host);

        Assert.Equal(["acme", "contoso", "meister-dev"], scopes);
    }

    [Fact]
    public async Task GitHubRepositories_SpanMoreThanOnePage_EveryRepositoryIsOffered()
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var factory = CreateFactory(
            "GitHubProvider", request => request.RequestUri!.AbsoluteUri switch
            {
                "https://api.github.com/user" => Json(new { login = "meister-dev" }),
                "https://api.github.com/orgs/acme/repos?per_page=100&page=1&type=all" => WithGitHubNextPage(
                    Json(new object[] { Repository(1, "acme/platform", "acme") })),
                "https://api.github.com/orgs/acme/repos?per_page=100&page=2&type=all" => Json(new object[] { Repository(2, "acme/tooling", "acme") }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var sut = new GitHubDiscoveryService(
            new GitHubConnectionVerifier(Connections(ScmProvider.GitHub, host), factory),
            factory);

        var repositories = await sut.ListRepositoriesAsync(ClientId, host, "acme");

        Assert.Equal(["1", "2"], repositories.Select(repository => repository.ExternalRepositoryId));
    }

    [Fact]
    public async Task GitLabProjects_SpanMoreThanOnePage_EveryProjectIsOffered()
    {
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var factory = CreateFactory(
            "GitLabProvider", request => request.RequestUri!.AbsoluteUri switch
            {
                "https://gitlab.example.com/api/v4/user" => Json(new { username = "meister-dev" }),
                "https://gitlab.example.com/api/v4/groups/acme/projects?per_page=100&page=1&include_subgroups=true&simple=true" =>
                    WithGitLabNextPage(Json(new object[] { Project(1, "acme/platform") }), "2"),
                "https://gitlab.example.com/api/v4/groups/acme/projects?per_page=100&page=2&include_subgroups=true&simple=true" =>
                    WithGitLabNextPage(Json(new object[] { Project(2, "acme/nested/api") }), string.Empty),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var sut = new GitLabDiscoveryService(
            new GitLabConnectionVerifier(Connections(ScmProvider.GitLab, host), factory),
            factory);

        var repositories = await sut.ListRepositoriesAsync(ClientId, host, "acme");

        Assert.Equal(["1", "2"], repositories.Select(repository => repository.ExternalRepositoryId));

        // A nested project keeps its path, which is what tells two same-named projects apart.
        Assert.Contains(repositories, repository => repository.ProjectPath == "acme/nested/api");
    }

    /// <summary>
    ///     Forgejo clamps a requested page size to the host's own maximum, so how much remains comes from the
    ///     total it reports rather than from counting what came back.
    /// </summary>
    [Fact]
    public async Task ForgejoRepositories_ClampedPages_EveryRepositoryIsOffered()
    {
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://codeberg.example.com");
        var factory = CreateFactory(
            "ForgejoProvider", request => request.RequestUri!.AbsoluteUri switch
            {
                "https://codeberg.example.com/api/v1/user" => Json(new { login = "meister-dev" }),
                "https://codeberg.example.com/api/v1/orgs/acme/repos?limit=100" => WithForgejoTotalCount(
                    Json(new object[] { Repository(1, "acme/platform", "acme") }),
                    2),
                "https://codeberg.example.com/api/v1/orgs/acme/repos?page=2&limit=100" => WithForgejoTotalCount(
                    Json(new object[] { Repository(2, "acme/tooling", "acme") }),
                    2),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var sut = new ForgejoDiscoveryService(
            new ForgejoConnectionVerifier(Connections(ScmProvider.Forgejo, host), factory),
            factory);

        var repositories = await sut.ListRepositoriesAsync(ClientId, host, "acme");

        Assert.Equal(["1", "2"], repositories.Select(repository => repository.ExternalRepositoryId));
    }

    private static object Repository(long id, string fullName, string owner)
    {
        return new { id, full_name = fullName, owner = new { login = owner } };
    }

    private static object Project(long id, string pathWithNamespace)
    {
        var separatorIndex = pathWithNamespace.LastIndexOf('/');
        return new
        {
            id,
            path_with_namespace = pathWithNamespace,
            @namespace = new { full_path = pathWithNamespace[..separatorIndex] },
        };
    }

    private static HttpResponseMessage WithGitHubNextPage(HttpResponseMessage response)
    {
        response.Headers.Add("Link", "<https://api.github.com/next>; rel=\"next\"");
        return response;
    }

    private static HttpResponseMessage WithGitLabNextPage(HttpResponseMessage response, string nextPage)
    {
        response.Headers.Add("X-Next-Page", nextPage);
        return response;
    }

    private static HttpResponseMessage WithForgejoTotalCount(HttpResponseMessage response, int totalCount)
    {
        response.Headers.Add("X-Total-Count", totalCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return response;
    }

    private static HttpResponseMessage Json<T>(T payload)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload)),
        };
    }

    private static IHttpClientFactory CreateFactory(
        string clientName,
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(clientName).Returns(new HttpClient(new StubHandler(responder)));
        return factory;
    }

    private static IClientScmConnectionRepository Connections(ScmProvider provider, ProviderHostRef host)
    {
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetOperationalConnectionAsync(ClientId, host, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    Guid.NewGuid(),
                    ClientId,
                    provider,
                    host.HostBaseUrl,
                    ScmAuthenticationKind.PersonalAccessToken,
                    provider.ToString(),
                    "provider-token",
                    true));
        return connections;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(responder(request));
        }
    }
}

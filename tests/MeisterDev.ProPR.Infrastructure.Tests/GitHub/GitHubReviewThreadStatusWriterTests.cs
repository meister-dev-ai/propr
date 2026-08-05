// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.GitHub;

public sealed class GitHubReviewThreadStatusWriterTests
{
    private const string ThreadNodeId = "PRRT_kwDOABCD1234";

    [Fact]
    public void ProviderAdapters_RegisterGitHubThreadStatusWriterUnderNeutralInterface()
    {
        var services = new ServiceCollection();

        services.AddSingleton(Substitute.For<IClientScmConnectionRepository>());
        services.AddGitHubProviderAdapters();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var statusWriter = scope.ServiceProvider
            .GetServices<IReviewThreadStatusWriter>()
            .Single(service => service.Provider == ScmProvider.GitHub);

        Assert.IsType<GitHubReviewThreadStatusWriter>(statusWriter);
    }

    [Fact]
    public async Task UpdateThreadStatusAsync_FixedStatus_ResolvesThreadThroughGraphQlMutation()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var thread = CreateThread(host);
        var connectionRepository = CreateConnectionRepository(clientId, host);

        string? mutationUri = null;
        string? mutationBody = null;
        var httpClientFactory = CreateHttpClientFactory(async request =>
        {
            if (request.RequestUri!.AbsoluteUri == "https://api.github.com/user")
            {
                return CreateJsonResponse(new { login = "meister-dev" });
            }

            mutationUri = request.RequestUri.AbsoluteUri;
            mutationBody = await request.Content!.ReadAsStringAsync();
            return CreateJsonResponse(new { data = new { resolveReviewThread = new { thread = new { id = ThreadNodeId, isResolved = true } } } });
        });

        var sut = new GitHubReviewThreadStatusWriter(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        await sut.UpdateThreadStatusAsync(clientId, thread, "fixed");

        Assert.Equal("https://api.github.com/graphql", mutationUri);
        Assert.NotNull(mutationBody);
        Assert.Contains("resolveReviewThread", mutationBody, StringComparison.Ordinal);
        Assert.DoesNotContain("unresolveReviewThread", mutationBody, StringComparison.Ordinal);
        Assert.Contains(ThreadNodeId, mutationBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateThreadStatusAsync_ActiveStatus_UnresolvesThread()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var thread = CreateThread(host);
        var connectionRepository = CreateConnectionRepository(clientId, host);

        string? mutationBody = null;
        var httpClientFactory = CreateHttpClientFactory(async request =>
        {
            if (request.RequestUri!.AbsoluteUri == "https://api.github.com/user")
            {
                return CreateJsonResponse(new { login = "meister-dev" });
            }

            mutationBody = await request.Content!.ReadAsStringAsync();
            return CreateJsonResponse(new { data = new { unresolveReviewThread = new { thread = new { id = ThreadNodeId, isResolved = false } } } });
        });

        var sut = new GitHubReviewThreadStatusWriter(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        await sut.UpdateThreadStatusAsync(clientId, thread, "active");

        Assert.NotNull(mutationBody);
        Assert.Contains("unresolveReviewThread", mutationBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateThreadStatusAsync_WhenGraphQlRefusesForPermissions_NamesTheMissingPermission()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var thread = CreateThread(host);
        var connectionRepository = CreateConnectionRepository(clientId, host);

        // GitHub answers a permission refusal with HTTP 200 and a FORBIDDEN entry in the errors array.
        var httpClientFactory = CreateHttpClientFactory(request => Task.FromResult(
            request.RequestUri!.AbsoluteUri == "https://api.github.com/user"
                ? CreateJsonResponse(new { login = "meister-dev" })
                : CreateJsonResponse(
                    new
                    {
                        data = (object?)null,
                        errors = new[]
                        {
                            new { type = "FORBIDDEN", message = "Resource not accessible by integration" },
                        },
                    })));

        var sut = new GitHubReviewThreadStatusWriter(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.UpdateThreadStatusAsync(clientId, thread, "fixed"));

        Assert.Contains("Resource not accessible by integration", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Contents read and write", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateThreadStatusAsync_WhenTokenIsRejected_SurfacesTheRejectionStatus()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var thread = CreateThread(host);
        var connectionRepository = CreateConnectionRepository(clientId, host);

        var httpClientFactory = CreateHttpClientFactory(request => Task.FromResult(
            request.RequestUri!.AbsoluteUri == "https://api.github.com/user"
                ? CreateJsonResponse(new { login = "meister-dev" })
                : new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("Bad credentials"),
                }));

        var sut = new GitHubReviewThreadStatusWriter(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.UpdateThreadStatusAsync(clientId, thread, "fixed"));

        Assert.Contains("401", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Contents read and write", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Bad credentials", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateThreadStatusAsync_WhenMutationLeavesTheThreadUnchanged_IsNotReportedAsSuccess()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var thread = CreateThread(host);
        var connectionRepository = CreateConnectionRepository(clientId, host);

        var httpClientFactory = CreateHttpClientFactory(request => Task.FromResult(
            request.RequestUri!.AbsoluteUri == "https://api.github.com/user"
                ? CreateJsonResponse(new { login = "meister-dev" })
                : CreateJsonResponse(new { data = new { resolveReviewThread = new { thread = new { id = ThreadNodeId, isResolved = false } } } })));

        var sut = new GitHubReviewThreadStatusWriter(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.UpdateThreadStatusAsync(clientId, thread, "fixed"));

        Assert.Contains("reports it as unresolved", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateThreadStatusAsync_StatusWithNoResolvedEquivalent_IsRefusedWithoutCallingGitHub()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var thread = CreateThread(host);
        var connectionRepository = CreateConnectionRepository(clientId, host);

        var requests = 0;
        var httpClientFactory = CreateHttpClientFactory(_ =>
        {
            requests++;
            return Task.FromResult(CreateJsonResponse(new { login = "meister-dev" }));
        });

        var sut = new GitHubReviewThreadStatusWriter(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.UpdateThreadStatusAsync(clientId, thread, "unknown"));

        Assert.Contains("has no equivalent", exception.Message, StringComparison.Ordinal);
        Assert.Contains("fixed", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task UpdateThreadStatusAsync_NumericThreadId_IsRefusedAsNotAReviewThread()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);

        // The publish path records a review id here, which cannot address a review thread.
        var thread = new ReviewThreadRef(review, "555", "src/file.ts", 18, isReviewerOwned: true);
        var connectionRepository = CreateConnectionRepository(clientId, host);
        var httpClientFactory = CreateHttpClientFactory(_ => Task.FromResult(CreateJsonResponse(new { login = "meister-dev" })));

        var sut = new GitHubReviewThreadStatusWriter(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.UpdateThreadStatusAsync(clientId, thread, "fixed"));

        Assert.Contains("GraphQL node id", exception.Message, StringComparison.Ordinal);
        Assert.Contains("555", exception.Message, StringComparison.Ordinal);
    }

    private static ReviewThreadRef CreateThread(ProviderHostRef host)
    {
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        return new ReviewThreadRef(review, ThreadNodeId, "src/file.ts", 18, isReviewerOwned: true);
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

    private static IHttpClientFactory CreateHttpClientFactory(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
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

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return responder(request);
        }
    }
}

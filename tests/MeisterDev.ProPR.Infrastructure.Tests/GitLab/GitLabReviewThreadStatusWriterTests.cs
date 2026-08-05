// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Security;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.GitLab;

public sealed class GitLabReviewThreadStatusWriterTests
{
    private const string DiscussionId = "6a9c1750b37d513a43987b574953fceb50b03ce7";

    [Fact]
    public void ProviderAdapters_RegisterGitLabThreadStatusWriterUnderNeutralInterface()
    {
        var services = new ServiceCollection();

        services.AddSingleton(Substitute.For<IClientScmConnectionRepository>());
        services.AddGitLabProviderAdapters();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var statusWriter = scope.ServiceProvider
            .GetServices<IReviewThreadStatusWriter>()
            .Single(service => service.Provider == ScmProvider.GitLab);

        Assert.IsType<GitLabReviewThreadStatusWriter>(statusWriter);
    }

    [Fact]
    public async Task UpdateThreadStatusAsync_FixedStatus_ResolvesTheDiscussion()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var thread = CreateThread(host);
        var connectionRepository = GitLabTestHelpers.CreateConnectionRepository(clientId, host);

        HttpMethod? resolveMethod = null;
        string? resolveUri = null;
        var httpClientFactory = GitLabTestHelpers.CreateHttpClientFactory(request =>
        {
            if (request.RequestUri!.AbsoluteUri == "https://gitlab.example.com/api/v4/user")
            {
                return GitLabTestHelpers.CreateJsonResponse(new { username = "meister-dev" });
            }

            resolveMethod = request.Method;
            resolveUri = request.RequestUri.AbsoluteUri;
            return GitLabTestHelpers.CreateJsonResponse(new { id = DiscussionId });
        });

        var sut = new GitLabReviewThreadStatusWriter(
            new GitLabConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        await sut.UpdateThreadStatusAsync(clientId, thread, "fixed");

        Assert.Equal(HttpMethod.Put, resolveMethod);
        Assert.Equal(
            $"https://gitlab.example.com/api/v4/projects/101/merge_requests/42/discussions/{DiscussionId}?resolved=true",
            resolveUri);
    }

    [Fact]
    public async Task UpdateThreadStatusAsync_ActiveStatus_ReopensTheDiscussion()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var thread = CreateThread(host);
        var connectionRepository = GitLabTestHelpers.CreateConnectionRepository(clientId, host);

        string? resolveUri = null;
        var httpClientFactory = GitLabTestHelpers.CreateHttpClientFactory(request =>
        {
            if (request.RequestUri!.AbsoluteUri == "https://gitlab.example.com/api/v4/user")
            {
                return GitLabTestHelpers.CreateJsonResponse(new { username = "meister-dev" });
            }

            resolveUri = request.RequestUri.AbsoluteUri;
            return GitLabTestHelpers.CreateJsonResponse(new { id = DiscussionId });
        });

        var sut = new GitLabReviewThreadStatusWriter(
            new GitLabConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        await sut.UpdateThreadStatusAsync(clientId, thread, "active");

        Assert.Equal(
            $"https://gitlab.example.com/api/v4/projects/101/merge_requests/42/discussions/{DiscussionId}?resolved=false",
            resolveUri);
    }

    [Fact]
    public async Task UpdateThreadStatusAsync_WhenForbidden_NamesTheRoleAndScopeRequired()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var thread = CreateThread(host);
        var connectionRepository = GitLabTestHelpers.CreateConnectionRepository(clientId, host);

        var httpClientFactory = GitLabTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri == "https://gitlab.example.com/api/v4/user"
                ? GitLabTestHelpers.CreateJsonResponse(new { username = "meister-dev" })
                : new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("{\"message\":\"403 Forbidden\"}"),
                });

        var sut = new GitLabReviewThreadStatusWriter(
            new GitLabConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.UpdateThreadStatusAsync(clientId, thread, "fixed"));

        Assert.Contains("Developer, Maintainer or Owner role", exception.Message, StringComparison.Ordinal);
        Assert.Contains("api scope", exception.Message, StringComparison.Ordinal);
        Assert.Contains("403 Forbidden", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateThreadStatusAsync_WhenDiscussionIsGone_SaysWhichThreadCouldNotBeFound()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var thread = CreateThread(host);
        var connectionRepository = GitLabTestHelpers.CreateConnectionRepository(clientId, host);

        var httpClientFactory = GitLabTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri == "https://gitlab.example.com/api/v4/user"
                ? GitLabTestHelpers.CreateJsonResponse(new { username = "meister-dev" })
                : new HttpResponseMessage(HttpStatusCode.NotFound));

        var sut = new GitLabReviewThreadStatusWriter(
            new GitLabConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.UpdateThreadStatusAsync(clientId, thread, "fixed"));

        Assert.Contains(DiscussionId, exception.Message, StringComparison.Ordinal);
        Assert.Contains("could not find", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateThreadStatusAsync_StatusWithNoResolvedEquivalent_IsRefusedWithoutCallingGitLab()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var thread = CreateThread(host);
        var connectionRepository = GitLabTestHelpers.CreateConnectionRepository(clientId, host);

        var requests = 0;
        var httpClientFactory = GitLabTestHelpers.CreateHttpClientFactory(_ =>
        {
            requests++;
            return GitLabTestHelpers.CreateJsonResponse(new { username = "meister-dev" });
        });

        var sut = new GitLabReviewThreadStatusWriter(
            new GitLabConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.UpdateThreadStatusAsync(clientId, thread, "unknown"));

        Assert.Contains("has no equivalent", exception.Message, StringComparison.Ordinal);
        Assert.Contains("wontfix", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, requests);
    }

    private static ReviewThreadRef CreateThread(ProviderHostRef host)
    {
        var repository = new RepositoryRef(host, "101", "acme/platform", "acme/platform/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "4201", 42);
        return new ReviewThreadRef(review, DiscussionId, "src/file.ts", 18, isReviewerOwned: true);
    }
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Runtime;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers.Common;

/// <summary>
///     The registry hands out one latest-revision reader per provider. On Azure DevOps a later revision is a higher
///     iteration number, and on GitHub, GitLab and Forgejo it is a head commit other than the queued one.
/// </summary>
public sealed class LatestRevisionReaderTests
{
    private static readonly Guid ClientId = Guid.Parse("0b8f6c2a-1d3e-4f5a-9b7c-2e4d6f8a0c1e");
    private const int PullRequestId = 42;
    private const string QueuedHead = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string LaterHead = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string BaseSha = "cccccccccccccccccccccccccccccccccccccccc";

    [Theory]
    [InlineData(8, true)]
    [InlineData(7, false)]
    [InlineData(6, false)]
    public async Task AzureDevOps_ComparesTheLatestIterationWithTheQueuedOne(int latestIterationId, bool movedPast)
    {
        var job = AdoJob(queuedIterationId: 7);
        var resolver = Substitute.For<IPullRequestIterationResolver>();
        resolver.GetLatestIterationIdAsync(
                ClientId,
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                PullRequestId,
                Arg.Any<CancellationToken>())
            .Returns(latestIterationId);

        var reader = new AdoLatestRevisionReader(resolver);

        Assert.Equal(movedPast, await reader.HasMovedPastAsync(job));
    }

    [Fact]
    public async Task AzureDevOps_WithoutAnIterationResolver_ReportsNoLaterRevision()
    {
        var reader = new AdoLatestRevisionReader();

        Assert.False(await reader.HasMovedPastAsync(AdoJob(queuedIterationId: 7)));
    }

    [Theory]
    [InlineData(ScmProvider.GitHub, "https://github.com", LaterHead, true)]
    [InlineData(ScmProvider.GitHub, "https://github.com", QueuedHead, false)]
    [InlineData(ScmProvider.GitLab, "https://gitlab.example.com", LaterHead, true)]
    [InlineData(ScmProvider.GitLab, "https://gitlab.example.com", QueuedHead, false)]
    [InlineData(ScmProvider.Forgejo, "https://forgejo.example", LaterHead, true)]
    [InlineData(ScmProvider.Forgejo, "https://forgejo.example", QueuedHead, false)]
    public async Task CommitProvider_ComparesTheLatestHeadWithTheQueuedOne(
        ScmProvider provider,
        string hostBaseUrl,
        string latestHead,
        bool movedPast)
    {
        var job = CommitJob(provider, hostBaseUrl);
        var queryService = QueryService(provider);
        queryService.GetLatestRevisionAsync(ClientId, job.CodeReviewReference, Arg.Any<CancellationToken>())
            .Returns(Revision(latestHead));

        var reader = new CommitLatestRevisionReader(queryService);

        Assert.Equal(provider, reader.Provider);
        Assert.Equal(movedPast, await reader.HasMovedPastAsync(job));
    }

    [Fact]
    public async Task CommitProvider_NoRevisionFromTheProvider_ReportsNoLaterRevision()
    {
        var job = CommitJob(ScmProvider.GitHub, "https://github.com");
        var queryService = QueryService(ScmProvider.GitHub);
        queryService.GetLatestRevisionAsync(ClientId, job.CodeReviewReference, Arg.Any<CancellationToken>())
            .Returns((ReviewRevision?)null);

        var reader = new CommitLatestRevisionReader(queryService);

        Assert.False(await reader.HasMovedPastAsync(job));
    }

    [Fact]
    public void Registry_ForAzureDevOps_HandsOutTheRegisteredReader()
    {
        var adoReader = new AdoLatestRevisionReader(Substitute.For<IPullRequestIterationResolver>());
        var registry = CreateRegistry(
            [adoReader],
            [QueryService(ScmProvider.AzureDevOps), QueryService(ScmProvider.GitHub)]);

        Assert.Same(adoReader, registry.GetLatestRevisionReader(ScmProvider.AzureDevOps));
    }

    [Theory]
    [InlineData(ScmProvider.GitHub, "https://github.com")]
    [InlineData(ScmProvider.GitLab, "https://gitlab.example.com")]
    [InlineData(ScmProvider.Forgejo, "https://forgejo.example")]
    public async Task Registry_ForACommitProvider_ReadsThroughThatProvidersReviewQueryAdapter(
        ScmProvider provider,
        string hostBaseUrl)
    {
        var queryServices = new[] { ScmProvider.AzureDevOps, ScmProvider.GitHub, ScmProvider.GitLab, ScmProvider.Forgejo }
            .ToDictionary(candidate => candidate, QueryService);
        var resolver = Substitute.For<IPullRequestIterationResolver>();
        var registry = CreateRegistry([new AdoLatestRevisionReader(resolver)], queryServices.Values);
        var job = CommitJob(provider, hostBaseUrl);
        queryServices[provider].GetLatestRevisionAsync(ClientId, job.CodeReviewReference, Arg.Any<CancellationToken>())
            .Returns(Revision(LaterHead));

        var reader = registry.GetLatestRevisionReader(provider);

        Assert.Equal(provider, reader.Provider);
        Assert.True(await reader.HasMovedPastAsync(job));
        foreach (var other in queryServices.Where(entry => entry.Key != provider))
        {
            await other.Value.DidNotReceiveWithAnyArgs().GetLatestRevisionAsync(default, default!, default);
        }

        await resolver.DidNotReceiveWithAnyArgs().GetLatestIterationIdAsync(default, default!, default!, default!, default, default);
    }

    [Fact]
    public void Registry_ForAProviderWithoutAReviewQueryAdapter_Refuses()
    {
        var registry = CreateRegistry([], []);

        Assert.Throws<InvalidOperationException>(() => registry.GetLatestRevisionReader(ScmProvider.GitHub));
    }

    [Fact]
    public async Task AzureDevOpsAdapters_RegisterAReaderOverTheIterationResolver()
    {
        var job = AdoJob(queuedIterationId: 7);
        var resolver = Substitute.For<IPullRequestIterationResolver>();
        resolver.GetLatestIterationIdAsync(
                ClientId,
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                PullRequestId,
                Arg.Any<CancellationToken>())
            .Returns(8);
        var services = new ServiceCollection();
        services.AddSingleton(resolver);
        services.AddAzureDevOpsProviderAdapters();
        await using var host = services.BuildServiceProvider();
        await using var scope = host.CreateAsyncScope();

        var reader = Assert.Single(scope.ServiceProvider.GetServices<ILatestRevisionReader>());

        Assert.Equal(ScmProvider.AzureDevOps, reader.Provider);
        Assert.True(await reader.HasMovedPastAsync(job));
    }

    [Fact]
    public async Task AzureDevOpsAdapters_WithoutAnIterationResolver_StillProvideTheReader()
    {
        // The iteration resolver is registered with the crawling services, and some modules compose the provider
        // adapters without them. The registry receives every registered reader, so a reader that could not be
        // created without the resolver would make the registry fail as well.
        var services = new ServiceCollection();
        services.AddAzureDevOpsProviderAdapters();
        await using var host = services.BuildServiceProvider();
        await using var scope = host.CreateAsyncScope();

        var reader = Assert.Single(scope.ServiceProvider.GetServices<ILatestRevisionReader>());

        Assert.False(await reader.HasMovedPastAsync(AdoJob(queuedIterationId: 7)));
    }

    private static ScmProviderRegistry CreateRegistry(
        IEnumerable<ILatestRevisionReader> readers,
        IEnumerable<ICodeReviewQueryService> queryServices)
    {
        return new ScmProviderRegistry(
            [],
            [],
            queryServices,
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            latestRevisionReaders: readers);
    }

    private static ICodeReviewQueryService QueryService(ScmProvider provider)
    {
        var queryService = Substitute.For<ICodeReviewQueryService>();
        queryService.Provider.Returns(provider);
        return queryService;
    }

    private static ReviewRevision Revision(string headSha)
    {
        return new ReviewRevision(headSha, BaseSha, null, headSha, $"{BaseSha}...{headSha}");
    }

    private static ThreadPassJob AdoJob(int queuedIterationId)
    {
        var revisionKey = queuedIterationId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new ThreadPassJob(
            Guid.NewGuid(),
            ClientId,
            "https://dev.azure.com/acme",
            "project",
            "repo-1",
            PullRequestId,
            queuedIterationId,
            revisionKey,
            $"{revisionKey}|17:1");
    }

    private static ThreadPassJob CommitJob(ScmProvider provider, string hostBaseUrl)
    {
        var review = new CodeReviewRef(
            new RepositoryRef(new ProviderHostRef(provider, hostBaseUrl), "101", "acme", "acme/widgets"),
            CodeReviewPlatformKind.PullRequest,
            "9001",
            PullRequestId);
        return new ThreadPassJob(
            review,
            Guid.NewGuid(),
            ClientId,
            $"{hostBaseUrl}/acme",
            "acme",
            "101",
            PullRequestId,
            1,
            QueuedHead,
            $"{QueuedHead}|17:1");
    }
}

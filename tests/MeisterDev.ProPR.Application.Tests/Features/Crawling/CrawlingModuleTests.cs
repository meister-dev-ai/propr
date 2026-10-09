// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.Crawling;

public sealed class CrawlingModuleTests
{
    [Fact]
    public void CrawlerConstruction_RequiresSharedSynchronization()
    {
        var constructor = Assert.Single(typeof(PrCrawlService).GetConstructors());
        var synchronization = Assert.Single(
            constructor.GetParameters(),
            parameter => parameter.ParameterType == typeof(MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports.IPullRequestSynchronizationService));

        Assert.False(synchronization.IsOptional);
        Assert.False(synchronization.HasDefaultValue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanonicalAdmissionRequiresRegisteredLocalSourcePolicy(bool registered)
    {
        var configs = Substitute.For<ICrawlConfigurationRepository>();
        var fetcher = Substitute.For<IAssignedReviewDiscoveryService>();
        var jobs = Substitute.For<IJobRepository>();
        var config = DefaultConfig with
        {
            RepoFilters = [new(Guid.NewGuid(), "repo", [], new("AzureDevOps", "repo-1"))]
        };
        configs.GetAllActiveAsync(Arg.Any<CancellationToken>()).Returns([config]);
        configs.GetReviewTargetPolicySnapshotAsync(config.Id, config.ClientId, Arg.Any<CancellationToken>()).Returns(config);
        configs.AcquireReviewTargetAdmissionAsync(config.ClientId, Arg.Any<CancellationToken>()).Returns(Substitute.For<IAsyncDisposable>());
        fetcher.ListAssignedOpenReviewsAsync(config, Arg.Any<CancellationToken>()).Returns([CreateAssignedReview(config, "repo-1", 7, 1)]);
        var registry = MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute();
        registry.GetReviewSourcePolicy(Arg.Any<ScmProvider>()).Returns(call =>
            MeisterDev.ProPR.TestSupport.ReviewSourcePolicies.Get(call.Arg<ScmProvider>()));
        var service = new PrCrawlService(
            configs, fetcher, jobs, Substitute.For<IPrStatusFetcher>(),
            NullLogger<PrCrawlService>.Instance, Synchronization(jobs), providerRegistry: registered ? registry : null);

        await service.CrawlAsync();

        await jobs.Received(registered ? 1 : 0).TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>());
    }

    [Fact]
    public async Task CrawlAsync_TargetDisabledDuringProviderReadDoesNotQueueWork()
    {
        var configs = Substitute.For<ICrawlConfigurationRepository>();
        var fetcher = Substitute.For<IAssignedReviewDiscoveryService>();
        var jobs = Substitute.For<IJobRepository>();
        var config = DefaultConfig with
        {
            RepoFilters = [new(Guid.NewGuid(), "repo", [], new("AzureDevOps", "repo-1"))]
        };
        configs.GetAllActiveAsync(Arg.Any<CancellationToken>()).Returns([config]);
        configs.AcquireReviewTargetAdmissionAsync(config.ClientId, Arg.Any<CancellationToken>()).Returns(Substitute.For<IAsyncDisposable>());
        configs.GetReviewTargetPolicySnapshotAsync(config.Id, config.ClientId, Arg.Any<CancellationToken>())
            .Returns(config with { IsActive = false, ReviewTargetLifecycle = ReviewTargetLifecycle.Disabled });
        fetcher.ListAssignedOpenReviewsAsync(config, Arg.Any<CancellationToken>()).Returns([CreateAssignedReview(config, "repo-1", 7, 1)]);
        var service = new PrCrawlService(
            configs, fetcher, jobs, Substitute.For<IPrStatusFetcher>(), NullLogger<PrCrawlService>.Instance, Synchronization(jobs));

        await service.CrawlAsync();

        await jobs.DidNotReceiveWithAnyArgs().TryAddIfNoActiveDuplicateAsync(default!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrawlAsync_PreparesBeforeAdmissionAndRechecksSettingsUnderReleasedLease(bool cancelCompletion)
    {
        var order = new List<string>();
        var configs = Substitute.For<ICrawlConfigurationRepository>();
        var discovery = Substitute.For<IAssignedReviewDiscoveryService>();
        var jobs = Substitute.For<IJobRepository>();
        var threads = Substitute.For<IReviewerThreadStatusFetcher>();
        var scans = Substitute.For<IReviewPrScanRepository>();
        var lease = Substitute.For<IAsyncDisposable>();
        using var cancellation = new CancellationTokenSource();
        var config = DefaultConfig with
        {
            RepoFilters = [new(Guid.NewGuid(), "repo", [], new("AzureDevOps", "repo-1"))],
            ReviewTemperature = 0.1f,
        };
        var sourceId = Guid.NewGuid();
        configs.GetAllActiveAsync(Arg.Any<CancellationToken>()).Returns([config]);
        discovery.ListAssignedOpenReviewsAsync(config, Arg.Any<CancellationToken>()).Returns([CreateAssignedReview(config, "repo-1", 7, 1)]);
        var scan = new ReviewPrScan(Guid.NewGuid(), config.ClientId, config.ProviderScopePath, config.ProviderProjectKey, "repo-1", 7, "1");
        scan.Threads.Add(new ReviewPrScanThread { ThreadId = "17", LastSeenStatus = "Active" });
        scans.GetAsync(config.ClientId, config.ProviderScopePath, config.ProviderProjectKey, "repo-1", 7, Arg.Any<CancellationToken>()).Returns(scan);
        threads.GetReviewerThreadStatusesAsync(
                config.ProviderScopePath, config.ProviderProjectKey, "repo-1", 7,
                Arg.Any<ThreadOwnershipResolver>(), config.ClientId, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                order.Add("prepare");
                return new[] { new PrThreadStatusEntry("17", "Active", "file.cs", "Reply", 1) };
            });
        configs.AcquireReviewTargetAdmissionAsync(config.ClientId, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            order.Add("acquire");
            return lease;
        });
        configs.GetReviewTargetPolicySnapshotAsync(config.Id, config.ClientId, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            order.Add("recheck");
            return config with
            {
                ProCursorSourceScopeMode = ProCursorSourceScopeMode.SelectedSources,
                ProCursorSourceIds = [sourceId],
                InvalidProCursorSourceIds = [],
                ReviewTemperature = 0.7f,
            };
        });
        ReviewJob? queued = null;
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            order.Add("complete");
            queued = call.Arg<ReviewJob>();
            if (cancelCompletion)
            {
                cancellation.Cancel();
                return Task.FromCanceled<TryAddReviewJobResult>(cancellation.Token);
            }

            return Task.FromResult(new TryAddReviewJobResult(true, null, 0));
        });
        lease.DisposeAsync().Returns(_ =>
        {
            order.Add("release");
            return ValueTask.CompletedTask;
        });
        var registry = MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute();
        registry.GetReviewSourcePolicy(Arg.Any<ScmProvider>()).Returns(call =>
            MeisterDev.ProPR.TestSupport.ReviewSourcePolicies.Get(call.Arg<ScmProvider>()));
        var synchronization = new PullRequestSynchronizationService(
            MeisterDev.ProPR.TestSupport.LocalScmPolicies.Registry,
            jobs, NullLogger<PullRequestSynchronizationService>.Instance,
            threadStatusFetcher: threads, threadMemoryService: Substitute.For<IThreadMemoryService>(), prScanRepository: scans);
        var crawler = new PrCrawlService(
            configs, discovery, jobs, Substitute.For<IPrStatusFetcher>(),
            NullLogger<PrCrawlService>.Instance, synchronization, providerRegistry: registry);

        if (cancelCompletion)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => crawler.CrawlAsync(cancellation.Token));
        }
        else
        {
            await crawler.CrawlAsync(cancellation.Token);
        }

        Assert.Equal(new[] { "prepare", "acquire", "recheck", "complete", "release" }, order);
        Assert.NotNull(queued);
        Assert.Equal(ProCursorSourceScopeMode.SelectedSources, queued.ProCursorSourceScopeMode);
        Assert.Equal(new[] { sourceId }, queued.ProCursorSourceIds);
        Assert.Equal(0.7f, queued.ReviewTemperature);
        await lease.Received(1).DisposeAsync();
    }

    private static readonly CrawlConfigurationDto DefaultConfig = new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        ScmProvider.AzureDevOps,
        "https://dev.azure.com/org",
        "proj",
        60,
        true,
        DateTimeOffset.UtcNow,
        []);

    [Fact]
    public async Task CrawlAsync_SelectedSourceScope_SnapshotsSelectedProCursorSourcesOnQueuedJob()
    {
        var crawlConfigs = Substitute.For<ICrawlConfigurationRepository>();
        var prFetcher = Substitute.For<IAssignedReviewDiscoveryService>();
        var jobs = Substitute.For<IJobRepository>();
        var statusFetcher = Substitute.For<IPrStatusFetcher>();
        var sut = new PrCrawlService(crawlConfigs, prFetcher, jobs, statusFetcher, NullLogger<PrCrawlService>.Instance, Synchronization(jobs));

        var sourceId = Guid.NewGuid();
        var config = DefaultConfig with
        {
            ProCursorSourceScopeMode = ProCursorSourceScopeMode.SelectedSources,
            ProCursorSourceIds = [sourceId],
            InvalidProCursorSourceIds = [],
        };
        var pr = CreateAssignedReview(config, "repo-1", 48, 2);

        crawlConfigs.GetAllActiveAsync(Arg.Any<CancellationToken>()).Returns([config]);
        prFetcher.ListAssignedOpenReviewsAsync(config).Returns([pr]);
        jobs.FindActiveJob(config.ClientId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns((ReviewJob?)null);

        await sut.CrawlAsync();

        await jobs.Received(1)
            .TryAddIfNoActiveDuplicateAsync(
                Arg.Is<ReviewJob>(job =>
                    job.PullRequestId == pr.CodeReview.Number &&
                    job.ProCursorSourceScopeMode == ProCursorSourceScopeMode.SelectedSources &&
                    job.ProCursorSourceIds.SequenceEqual(new[] { sourceId })));
    }

    [Fact]
    public async Task CrawlAsync_InvalidSelectedSources_DoesNotQueueJob()
    {
        var crawlConfigs = Substitute.For<ICrawlConfigurationRepository>();
        var prFetcher = Substitute.For<IAssignedReviewDiscoveryService>();
        var jobs = Substitute.For<IJobRepository>();
        var statusFetcher = Substitute.For<IPrStatusFetcher>();
        var sut = new PrCrawlService(crawlConfigs, prFetcher, jobs, statusFetcher, NullLogger<PrCrawlService>.Instance, Synchronization(jobs));

        var config = DefaultConfig with
        {
            ProCursorSourceScopeMode = ProCursorSourceScopeMode.SelectedSources,
            ProCursorSourceIds = [Guid.NewGuid()],
            InvalidProCursorSourceIds = [Guid.NewGuid()],
        };

        crawlConfigs.GetAllActiveAsync(Arg.Any<CancellationToken>()).Returns([config]);
        prFetcher.ListAssignedOpenReviewsAsync(config)
            .Returns([CreateAssignedReview(config, "repo-1", 49, 1)]);
        jobs.FindActiveJob(config.ClientId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns((ReviewJob?)null);

        await sut.CrawlAsync();

        await jobs.DidNotReceive().TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>());
    }

    private static PullRequestSynchronizationService Synchronization(IJobRepository jobs)
    {
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryAddReviewJobResult(true, null, 0));
        return new PullRequestSynchronizationService(
            MeisterDev.ProPR.TestSupport.LocalScmPolicies.Registry, jobs, NullLogger<PullRequestSynchronizationService>.Instance);
    }

    private static AssignedCodeReviewRef CreateAssignedReview(
        CrawlConfigurationDto config,
        string repositoryId,
        int reviewNumber,
        int revisionId)
    {
        var host = new ProviderHostRef(config.Provider, config.ProviderScopePath);
        var repository = new RepositoryRef(host, repositoryId, config.ProviderProjectKey, config.ProviderProjectKey);
        var review = new CodeReviewRef(
            repository,
            CodeReviewPlatformKind.PullRequest,
            reviewNumber.ToString(),
            reviewNumber);

        return new AssignedCodeReviewRef(host, repository, review, revisionId);
    }
}

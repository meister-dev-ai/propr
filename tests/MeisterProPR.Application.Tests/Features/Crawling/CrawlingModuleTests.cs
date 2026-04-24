// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Services;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Application.Tests.Features.Crawling;

public sealed class CrawlingModuleTests
{
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
        var sut = new PrCrawlService(crawlConfigs, prFetcher, jobs, statusFetcher, NullLogger<PrCrawlService>.Instance);

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
        jobs.FindActiveJob(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns((ReviewJob?)null);

        await sut.CrawlAsync();

        await jobs.Received(1)
            .AddAsync(
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
        var sut = new PrCrawlService(crawlConfigs, prFetcher, jobs, statusFetcher, NullLogger<PrCrawlService>.Instance);

        var config = DefaultConfig with
        {
            ProCursorSourceScopeMode = ProCursorSourceScopeMode.SelectedSources,
            ProCursorSourceIds = [Guid.NewGuid()],
            InvalidProCursorSourceIds = [Guid.NewGuid()],
        };

        crawlConfigs.GetAllActiveAsync(Arg.Any<CancellationToken>()).Returns([config]);
        prFetcher.ListAssignedOpenReviewsAsync(config)
            .Returns([CreateAssignedReview(config, "repo-1", 49, 1)]);
        jobs.FindActiveJob(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns((ReviewJob?)null);

        await sut.CrawlAsync();

        await jobs.DidNotReceive().AddAsync(Arg.Any<ReviewJob>());
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

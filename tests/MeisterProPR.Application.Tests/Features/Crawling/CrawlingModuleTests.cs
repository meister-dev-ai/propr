// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Services;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Application.Tests.Features.Crawling;

public sealed class CrawlingModuleTests
{
    private static readonly CrawlConfigurationDto DefaultConfig = new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "https://dev.azure.com/org",
        "proj",
        Guid.NewGuid(),
        60,
        true,
        DateTimeOffset.UtcNow,
        []);

    [Fact]
    public async Task CrawlAsync_SelectedSourceScope_SnapshotsSelectedProCursorSourcesOnQueuedJob()
    {
        var crawlConfigs = Substitute.For<ICrawlConfigurationRepository>();
        var prFetcher = Substitute.For<IAssignedPrFetcher>();
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
        var pr = new AssignedPullRequestRef(config.OrganizationUrl, config.ProjectId, "repo-1", 48, 2);

        crawlConfigs.GetAllActiveAsync(Arg.Any<CancellationToken>()).Returns([config]);
        prFetcher.GetAssignedOpenPullRequestsAsync(config).Returns([pr]);
        jobs.FindActiveJob(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns((ReviewJob?)null);

        await sut.CrawlAsync();

        await jobs.Received(1).AddAsync(
            Arg.Is<ReviewJob>(job =>
                job.PullRequestId == pr.PullRequestId &&
                job.ProCursorSourceScopeMode == ProCursorSourceScopeMode.SelectedSources &&
                job.ProCursorSourceIds.SequenceEqual(new[] { sourceId })));
    }

    [Fact]
    public async Task CrawlAsync_InvalidSelectedSources_DoesNotQueueJob()
    {
        var crawlConfigs = Substitute.For<ICrawlConfigurationRepository>();
        var prFetcher = Substitute.For<IAssignedPrFetcher>();
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
        prFetcher.GetAssignedOpenPullRequestsAsync(config)
            .Returns([new AssignedPullRequestRef(config.OrganizationUrl, config.ProjectId, "repo-1", 49, 1)]);
        jobs.FindActiveJob(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns((ReviewJob?)null);

        await sut.CrawlAsync();

        await jobs.DidNotReceive().AddAsync(Arg.Any<ReviewJob>());
    }
}

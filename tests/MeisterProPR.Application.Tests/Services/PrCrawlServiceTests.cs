using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Services;
using MeisterProPR.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Application.Tests.Services;

/// <summary>Unit tests for <see cref="PrCrawlService" /> using NSubstitute.</summary>
public sealed class PrCrawlServiceTests
{
    private static readonly CrawlConfigurationDto DefaultConfig = new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "https://dev.azure.com/org",
        "proj",
        Guid.NewGuid(),
        60,
        true,
        DateTimeOffset.UtcNow);

    private readonly ICrawlConfigurationRepository _crawlConfigs = Substitute.For<ICrawlConfigurationRepository>();
    private readonly IJobRepository _jobs = Substitute.For<IJobRepository>();

    private readonly IAssignedPullRequestFetcher _prFetcher = Substitute.For<IAssignedPullRequestFetcher>();
    private readonly PrCrawlService _sut;

    public PrCrawlServiceTests()
    {
        this._sut = new PrCrawlService(
            this._crawlConfigs,
            this._prFetcher,
            this._jobs,
            NullLogger<PrCrawlService>.Instance);
    }

    private static AssignedPullRequestRef MakePr(int prId = 1, int iterationId = 1)
    {
        return new AssignedPullRequestRef(
            DefaultConfig.OrganizationUrl,
            DefaultConfig.ProjectId,
            "repo-1",
            prId,
            iterationId);
    }

    [Fact]
    public async Task CrawlAsync_AssignedPrWithExistingActiveJob_DoesNotAddJob()
    {
        // Arrange
        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        var pr = MakePr(99);
        this._prFetcher.GetAssignedOpenPullRequestsAsync(DefaultConfig).ReturnsForAnyArgs([pr]);

        // FindActiveJob returns an existing job (Pending/Processing/Completed)
        var existingJob = new ReviewJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            pr.OrganizationUrl,
            pr.ProjectId,
            pr.RepositoryId,
            pr.PullRequestId,
            pr.LatestIterationId);
        this._jobs.FindActiveJob(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>())
            .Returns(existingJob);

        // Act
        await this._sut.CrawlAsync();

        // Assert: Add was NOT called
        await this._jobs.DidNotReceive().AddAsync(Arg.Any<ReviewJob>());
    }

    [Fact]
    public async Task CrawlAsync_AssignedPrWithFailedJob_AddsNewJob()
    {
        // Arrange: FindActiveJob returns null for Failed jobs (idempotency rule)
        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        var pr = MakePr(77);
        this._prFetcher.GetAssignedOpenPullRequestsAsync(DefaultConfig).ReturnsForAnyArgs([pr]);
        this._jobs.FindActiveJob(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>())
            .Returns((ReviewJob?)null); // null = no active job (Failed is excluded by repo)

        // Act
        await this._sut.CrawlAsync();

        // Assert: a new job is created
        await this._jobs.Received(1).AddAsync(Arg.Any<ReviewJob>());
    }

    [Fact]
    public async Task CrawlAsync_AssignedPrWithNoActiveJob_AddsJob()
    {
        // Arrange
        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        var pr = MakePr(42);
        this._prFetcher.GetAssignedOpenPullRequestsAsync(DefaultConfig).ReturnsForAnyArgs([pr]);
        this._jobs.FindActiveJob(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>())
            .Returns((ReviewJob?)null);

        // Act
        await this._sut.CrawlAsync();

        // Assert: Add was called exactly once with the config's ClientId
        await this._jobs.Received(1)
            .AddAsync(
                Arg.Is<ReviewJob>(j =>
                    j.PullRequestId == 42 &&
                    j.IterationId == 1 &&
                    j.ClientId == DefaultConfig.ClientId));
    }

    [Fact]
    public async Task CrawlAsync_FetchThrows_SkipsConfigAndContinues()
    {
        // Arrange: one config throws, another succeeds
        var config2 = DefaultConfig with { Id = Guid.NewGuid(), ProjectId = "proj-ok" };
        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig, config2]);

        // First config throws (faulted Task)
        this._prFetcher.GetAssignedOpenPullRequestsAsync(
                Arg.Is<CrawlConfigurationDto>(c => c.ProjectId == DefaultConfig.ProjectId),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<AssignedPullRequestRef>>(new Exception("ADO error")));

        // Second config succeeds
        this._prFetcher.GetAssignedOpenPullRequestsAsync(
                Arg.Is<CrawlConfigurationDto>(c => c.ProjectId == config2.ProjectId),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AssignedPullRequestRef>>([MakePr(55)]));

        this._jobs.FindActiveJob(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>())
            .Returns((ReviewJob?)null);

        // Act — must not throw
        await this._sut.CrawlAsync();

        // Assert: job created for the successful config only
        await this._jobs.Received(1).AddAsync(Arg.Any<ReviewJob>());
    }

    [Fact]
    public async Task CrawlAsync_MultipleCrawlConfigs_EachIsProcessed()
    {
        // Arrange
        var config2 = DefaultConfig with { Id = Guid.NewGuid(), ProjectId = "proj-2" };
        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig, config2]);
        var pr1 = MakePr(10);
        var pr2 = new AssignedPullRequestRef(config2.OrganizationUrl, config2.ProjectId, "repo-2", 20, 1);
        this._prFetcher.GetAssignedOpenPullRequestsAsync(DefaultConfig).ReturnsForAnyArgs([pr1]);
        this._prFetcher.GetAssignedOpenPullRequestsAsync(config2).ReturnsForAnyArgs([pr2]);
        this._jobs.FindActiveJob(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>())
            .Returns((ReviewJob?)null);

        // Act
        await this._sut.CrawlAsync();

        // Assert: a job created for each discovered PR
        await this._jobs.Received(2).AddAsync(Arg.Any<ReviewJob>());
    }
}

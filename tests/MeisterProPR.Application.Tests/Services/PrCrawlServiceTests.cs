// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Services;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
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
        DateTimeOffset.UtcNow,
        []);

    private readonly ICrawlConfigurationRepository _crawlConfigs = Substitute.For<ICrawlConfigurationRepository>();
    private readonly IJobRepository _jobs = Substitute.For<IJobRepository>();

    private readonly IAssignedPrFetcher _prFetcher = Substitute.For<IAssignedPrFetcher>();
    private readonly IPrStatusFetcher _statusFetcher = Substitute.For<IPrStatusFetcher>();
    private readonly IThreadMemoryService _threadMemoryService = Substitute.For<IThreadMemoryService>();
    private readonly IReviewerThreadStatusFetcher _threadStatusFetcher = Substitute.For<IReviewerThreadStatusFetcher>();
    private readonly IReviewPrScanRepository _prScanRepository = Substitute.For<IReviewPrScanRepository>();
    private readonly PrCrawlService _sut;

    public PrCrawlServiceTests()
    {
        this._sut = new PrCrawlService(
            this._crawlConfigs,
            this._prFetcher,
            this._jobs,
            this._statusFetcher,
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

    private static ReviewPrScan MakeScan(int prId, int iterationId, params ReviewPrScanThread[] threads)
    {
        var scan = new ReviewPrScan(
            Guid.NewGuid(),
            DefaultConfig.ClientId,
            "repo-1",
            prId,
            iterationId.ToString());

        foreach (var thread in threads)
        {
            scan.Threads.Add(thread);
        }

        return scan;
    }

    private PrCrawlService CreateSutWithScanDependencies()
    {
        return new PrCrawlService(
            this._crawlConfigs,
            this._prFetcher,
            this._jobs,
            this._statusFetcher,
            NullLogger<PrCrawlService>.Instance,
            this._threadStatusFetcher,
            this._threadMemoryService,
            this._prScanRepository);
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
    public async Task CrawlAsync_SelectedSourceScope_SnapshotsSelectedProCursorSourcesOnQueuedJob()
    {
        var sourceId = Guid.NewGuid();
        var config = DefaultConfig with
        {
            ProCursorSourceScopeMode = ProCursorSourceScopeMode.SelectedSources,
            ProCursorSourceIds = [sourceId],
            InvalidProCursorSourceIds = [],
        };

        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([config]);
        var pr = MakePr(48);
        this._prFetcher.GetAssignedOpenPullRequestsAsync(config).ReturnsForAnyArgs([pr]);
        this._jobs.FindActiveJob(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns((ReviewJob?)null);

        await this._sut.CrawlAsync();

        await this._jobs.Received(1).AddAsync(
            Arg.Is<ReviewJob>(job =>
                job.PullRequestId == pr.PullRequestId &&
                job.ProCursorSourceScopeMode == ProCursorSourceScopeMode.SelectedSources &&
                job.ProCursorSourceIds.SequenceEqual(new[] { sourceId })));
    }

    [Fact]
    public async Task CrawlAsync_InvalidSelectedSourceAssociations_DoesNotAddJob()
    {
        var config = DefaultConfig with
        {
            ProCursorSourceScopeMode = ProCursorSourceScopeMode.SelectedSources,
            ProCursorSourceIds = [Guid.NewGuid()],
            InvalidProCursorSourceIds = [Guid.NewGuid()],
        };

        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([config]);
        this._prFetcher.GetAssignedOpenPullRequestsAsync(config).ReturnsForAnyArgs([MakePr(49)]);
        this._jobs.FindActiveJob(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns((ReviewJob?)null);

        await this._sut.CrawlAsync();

        await this._jobs.DidNotReceive().AddAsync(Arg.Any<ReviewJob>());
    }

    [Fact]
    public async Task CrawlAsync_SameIterationWithoutNewReplies_DoesNotAddJob()
    {
        // Arrange
        var sut = this.CreateSutWithScanDependencies();
        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        var pr = MakePr(42, iterationId: 3);
        this._prFetcher.GetAssignedOpenPullRequestsAsync(DefaultConfig).ReturnsForAnyArgs([pr]);
        this._jobs.FindActiveJob(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>())
            .Returns((ReviewJob?)null);
        this._prScanRepository.GetAsync(DefaultConfig.ClientId, pr.RepositoryId, pr.PullRequestId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewPrScan?>(MakeScan(
                pr.PullRequestId,
                pr.LatestIterationId,
                new ReviewPrScanThread { ThreadId = 7, LastSeenReplyCount = 1, LastSeenStatus = "Active" })));
        this._threadStatusFetcher.GetReviewerThreadStatusesAsync(
                pr.OrganizationUrl,
                pr.ProjectId,
                pr.RepositoryId,
                pr.PullRequestId,
                DefaultConfig.ReviewerId!.Value,
                DefaultConfig.ClientId,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PrThreadStatusEntry>>([
                new PrThreadStatusEntry(7, "Active", "/src/file.ts", "Bot: initial comment\nUser: follow-up", 1),
            ]));

        // Act
        await sut.CrawlAsync();

        // Assert
        await this._jobs.DidNotReceive().AddAsync(Arg.Any<ReviewJob>());
    }

    [Fact]
    public async Task CrawlAsync_SameIterationWithoutNewReplies_DoesNotRecordNoOpActivity()
    {
        var sut = this.CreateSutWithScanDependencies();
        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        var pr = MakePr(42, iterationId: 3);
        this._prFetcher.GetAssignedOpenPullRequestsAsync(DefaultConfig).ReturnsForAnyArgs([pr]);
        this._jobs.FindActiveJob(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>())
            .Returns((ReviewJob?)null);
        this._prScanRepository.GetAsync(DefaultConfig.ClientId, pr.RepositoryId, pr.PullRequestId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewPrScan?>(MakeScan(
                pr.PullRequestId,
                pr.LatestIterationId,
                new ReviewPrScanThread { ThreadId = 7, LastSeenReplyCount = 1, LastSeenStatus = "Active" })));
        this._threadStatusFetcher.GetReviewerThreadStatusesAsync(
                pr.OrganizationUrl,
                pr.ProjectId,
                pr.RepositoryId,
                pr.PullRequestId,
                DefaultConfig.ReviewerId!.Value,
                DefaultConfig.ClientId,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PrThreadStatusEntry>>([
                new PrThreadStatusEntry(7, "Active", "/src/file.ts", "Bot: initial comment\nUser: follow-up", 1),
            ]));

        await sut.CrawlAsync();

        await this._threadMemoryService.DidNotReceive().RecordNoOpAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<string?>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CrawlAsync_SameIterationWithNewReplies_AddsJob()
    {
        // Arrange
        var sut = this.CreateSutWithScanDependencies();
        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        var pr = MakePr(43, iterationId: 5);
        this._prFetcher.GetAssignedOpenPullRequestsAsync(DefaultConfig).ReturnsForAnyArgs([pr]);
        this._jobs.FindActiveJob(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>())
            .Returns((ReviewJob?)null);
        this._prScanRepository.GetAsync(DefaultConfig.ClientId, pr.RepositoryId, pr.PullRequestId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewPrScan?>(MakeScan(
                pr.PullRequestId,
                pr.LatestIterationId,
                new ReviewPrScanThread { ThreadId = 8, LastSeenReplyCount = 0, LastSeenStatus = "Active" })));
        this._threadStatusFetcher.GetReviewerThreadStatusesAsync(
                pr.OrganizationUrl,
                pr.ProjectId,
                pr.RepositoryId,
                pr.PullRequestId,
                DefaultConfig.ReviewerId!.Value,
                DefaultConfig.ClientId,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PrThreadStatusEntry>>([
                new PrThreadStatusEntry(8, "Active", "/src/file.ts", "Bot: initial comment\nUser: new reply", 1),
            ]));

        // Act
        await sut.CrawlAsync();

        // Assert
        await this._jobs.Received(1)
            .AddAsync(Arg.Is<ReviewJob>(j => j.PullRequestId == pr.PullRequestId && j.IterationId == pr.LatestIterationId));
    }

    [Fact]
    public async Task CrawlAsync_NewIterationStillAddsJob_WhenScanExists()
    {
        // Arrange
        var sut = this.CreateSutWithScanDependencies();
        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        var pr = MakePr(44, iterationId: 6);
        this._prFetcher.GetAssignedOpenPullRequestsAsync(DefaultConfig).ReturnsForAnyArgs([pr]);
        this._jobs.FindActiveJob(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>())
            .Returns((ReviewJob?)null);
        this._prScanRepository.GetAsync(DefaultConfig.ClientId, pr.RepositoryId, pr.PullRequestId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewPrScan?>(MakeScan(pr.PullRequestId, iterationId: 5)));

        // Act
        await sut.CrawlAsync();

        // Assert
        await this._jobs.Received(1)
            .AddAsync(Arg.Is<ReviewJob>(j => j.PullRequestId == pr.PullRequestId && j.IterationId == pr.LatestIterationId));
    }

    [Fact]
    public async Task CrawlAsync_MissingScanStillAddsJob_ForRecovery()
    {
        // Arrange
        var sut = this.CreateSutWithScanDependencies();
        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        var pr = MakePr(45, iterationId: 2);
        this._prFetcher.GetAssignedOpenPullRequestsAsync(DefaultConfig).ReturnsForAnyArgs([pr]);
        this._jobs.FindActiveJob(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>())
            .Returns((ReviewJob?)null);
        this._prScanRepository.GetAsync(DefaultConfig.ClientId, pr.RepositoryId, pr.PullRequestId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewPrScan?>(null));

        // Act
        await sut.CrawlAsync();

        // Assert
        await this._jobs.Received(1)
            .AddAsync(Arg.Is<ReviewJob>(j => j.PullRequestId == pr.PullRequestId && j.IterationId == pr.LatestIterationId));
    }

    [Fact]
    public async Task CrawlAsync_MissingScanButCompletedSameIterationExists_DoesNotAddJob()
    {
        // Arrange
        var sut = this.CreateSutWithScanDependencies();
        var pr = MakePr(46, iterationId: 2);
        var completedJob = new ReviewJob(
            Guid.NewGuid(),
            DefaultConfig.ClientId,
            pr.OrganizationUrl,
            pr.ProjectId,
            pr.RepositoryId,
            pr.PullRequestId,
            pr.LatestIterationId)
        {
            Status = JobStatus.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
        };

        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        this._prFetcher.GetAssignedOpenPullRequestsAsync(DefaultConfig).ReturnsForAnyArgs([pr]);
        this._jobs.FindActiveJob(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>())
            .Returns((ReviewJob?)null);
        this._jobs.FindCompletedJob(
                pr.OrganizationUrl,
                pr.ProjectId,
                pr.RepositoryId,
                pr.PullRequestId,
                pr.LatestIterationId)
            .Returns(completedJob);
        this._prScanRepository.GetAsync(DefaultConfig.ClientId, pr.RepositoryId, pr.PullRequestId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewPrScan?>(null));

        // Act
        await sut.CrawlAsync();

        // Assert
        await this._jobs.DidNotReceive().AddAsync(Arg.Any<ReviewJob>());
    }

    [Fact]
    public async Task CrawlAsync_SameIterationWithNewRepliesAndCompletedJob_AddsJob()
    {
        // Arrange
        var sut = this.CreateSutWithScanDependencies();
        var pr = MakePr(47, iterationId: 5);
        var completedJob = new ReviewJob(
            Guid.NewGuid(),
            DefaultConfig.ClientId,
            pr.OrganizationUrl,
            pr.ProjectId,
            pr.RepositoryId,
            pr.PullRequestId,
            pr.LatestIterationId)
        {
            Status = JobStatus.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
        };

        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        this._prFetcher.GetAssignedOpenPullRequestsAsync(DefaultConfig).ReturnsForAnyArgs([pr]);
        this._jobs.FindActiveJob(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>())
            .Returns((ReviewJob?)null);
        this._jobs.FindCompletedJob(
                pr.OrganizationUrl,
                pr.ProjectId,
                pr.RepositoryId,
                pr.PullRequestId,
                pr.LatestIterationId)
            .Returns(completedJob);
        this._prScanRepository.GetAsync(DefaultConfig.ClientId, pr.RepositoryId, pr.PullRequestId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewPrScan?>(MakeScan(
                pr.PullRequestId,
                pr.LatestIterationId,
                new ReviewPrScanThread { ThreadId = 9, LastSeenReplyCount = 0, LastSeenStatus = "Active" })));
        this._threadStatusFetcher.GetReviewerThreadStatusesAsync(
                pr.OrganizationUrl,
                pr.ProjectId,
                pr.RepositoryId,
                pr.PullRequestId,
                DefaultConfig.ReviewerId!.Value,
                DefaultConfig.ClientId,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PrThreadStatusEntry>>([
                new PrThreadStatusEntry(9, "Active", "/src/file.ts", "Bot: initial comment\nUser: new reply", 1),
            ]));

        // Act
        await sut.CrawlAsync();

        // Assert
        await this._jobs.Received(1)
            .AddAsync(Arg.Is<ReviewJob>(j => j.PullRequestId == pr.PullRequestId && j.IterationId == pr.LatestIterationId));
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

    // --- T011: Abandonment detection tests (failing until T018 is implemented) ---

    [Fact]
    public async Task CrawlAsync_OrphanJobForAbandonedPr_CallsSetCancelledAsync()
    {
        // Arrange: an active job for PR 99 exists, but PR 99 is NOT in the discovered list
        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        var orphanJob = new ReviewJob(
            Guid.NewGuid(),
            DefaultConfig.ClientId,
            DefaultConfig.OrganizationUrl,
            DefaultConfig.ProjectId,
            "repo-1",
            99,
            1);
        // discovered list has only PR 101, not 99
        this._prFetcher.GetAssignedOpenPullRequestsAsync(DefaultConfig, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AssignedPullRequestRef>>([MakePr(101)]));
        this._jobs.FindActiveJob(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns((ReviewJob?)null);
        // active jobs for the config include the orphan
        this._jobs.GetActiveJobsForConfigAsync(DefaultConfig.OrganizationUrl, DefaultConfig.ProjectId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReviewJob>>([orphanJob]));
        // ADO reports the PR is Abandoned
        this._statusFetcher.GetStatusAsync(
                DefaultConfig.OrganizationUrl,
                DefaultConfig.ProjectId,
                orphanJob.RepositoryId,
                orphanJob.PullRequestId,
                DefaultConfig.ClientId,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PrStatus.Abandoned));

        // Act
        await this._sut.CrawlAsync();

        // Assert: SetCancelledAsync was called for the orphan job
        await this._jobs.Received(1).SetCancelledAsync(orphanJob.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CrawlAsync_OrphanJobForActivePr_DoesNotCallSetCancelledAsync()
    {
        // Arrange: active job for PR 99, not in discovered list, but ADO says Active
        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        var orphanJob = new ReviewJob(
            Guid.NewGuid(),
            DefaultConfig.ClientId,
            DefaultConfig.OrganizationUrl,
            DefaultConfig.ProjectId,
            "repo-1",
            99,
            1);
        this._prFetcher.GetAssignedOpenPullRequestsAsync(DefaultConfig, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AssignedPullRequestRef>>([MakePr(101)]));
        this._jobs.FindActiveJob(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns((ReviewJob?)null);
        this._jobs.GetActiveJobsForConfigAsync(DefaultConfig.OrganizationUrl, DefaultConfig.ProjectId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReviewJob>>([orphanJob]));
        // ADO says still Active (reviewer check returned nothing; missing from assign list is a race)
        this._statusFetcher.GetStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PrStatus.Active));

        // Act
        await this._sut.CrawlAsync();

        // Assert: no cancellation
        await this._jobs.DidNotReceive().SetCancelledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CrawlAsync_CompletedJobNotInDiscoveredList_DoesNotCallSetCancelledAsync()
    {
        // Arrange: only Pending/Processing jobs are candidates for cancellation; Completed jobs are left alone
        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        this._prFetcher.GetAssignedOpenPullRequestsAsync(DefaultConfig, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AssignedPullRequestRef>>([]));
        this._jobs.FindActiveJob(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns((ReviewJob?)null);
        // GetActiveJobsForConfigAsync returns empty (Completed jobs are not "active")
        this._jobs.GetActiveJobsForConfigAsync(DefaultConfig.OrganizationUrl, DefaultConfig.ProjectId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReviewJob>>([]));

        // Act
        await this._sut.CrawlAsync();

        // Assert: status fetcher never called; no cancellation
        await this._statusFetcher.DidNotReceiveWithAnyArgs()
            .GetStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        await this._jobs.DidNotReceive().SetCancelledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CrawlAsync_StatusFetcherThrowsForOrphanJob_DoesNotCallSetCancelledAsync()
    {
        // Arrange: fetcher throws (rate-limit etc.) → should be treated as Active, no cancellation
        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        var orphanJob = new ReviewJob(
            Guid.NewGuid(),
            DefaultConfig.ClientId,
            DefaultConfig.OrganizationUrl,
            DefaultConfig.ProjectId,
            "repo-1",
            99,
            1);
        this._prFetcher.GetAssignedOpenPullRequestsAsync(DefaultConfig, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AssignedPullRequestRef>>([MakePr(42)]));
        this._jobs.FindActiveJob(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns((ReviewJob?)null);
        this._jobs.GetActiveJobsForConfigAsync(DefaultConfig.OrganizationUrl, DefaultConfig.ProjectId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReviewJob>>([orphanJob]));
        // Note: IPrStatusFetcher is expected to swallow exceptions itself (fail-safe contract).
        // So even if the underlying ADO call throws, the interface implementation returns Active.
        // This test verifies no cancellation happens when the fetcher returns Active.
        this._statusFetcher.GetStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PrStatus.Active));

        // Act — must not throw
        await this._sut.CrawlAsync();

        // Assert: no cancellation
        await this._jobs.DidNotReceive().SetCancelledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // T038 — verify that PrCrawlService creates jobs only for PRs returned by the fetcher
    // (filtering is delegated to IAssignedPrFetcher — PrCrawlService itself just processes the list)

    [Fact]
    public async Task CrawlAsync_FetcherReturnsFilteredPrs_CreatesJobsOnlyForReturnedPrs()
    {
        // Arrange: fetcher returns only 1 of 2 discovered PRs (simulates repo/branch filter applied in fetcher)
        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);

        var filteredPr = MakePr(100, iterationId: 1);
        // Fetcher returns only the filtered PR (non-matching ones are excluded by the fetcher)
        this._prFetcher.GetAssignedOpenPullRequestsAsync(DefaultConfig).ReturnsForAnyArgs([filteredPr]);
        this._jobs.FindActiveJob(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>())
            .Returns((ReviewJob?)null);

        // Act
        await this._sut.CrawlAsync();

        // Assert: exactly one job created, for the filtered PR
        await this._jobs.Received(1)
            .AddAsync(
                Arg.Is<ReviewJob>(j =>
                    j.PullRequestId == 100 &&
                    j.ClientId == DefaultConfig.ClientId));
    }

    [Fact]
    public async Task CrawlAsync_PrContextPopulatedFromAssignedRef_WhenFieldsPresent()
    {
        // T038/T047: PrCrawlService calls UpdatePrContextAsync when the AssignedPullRequestRef
        // has PR context fields populated.
        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);

        var prWithContext = new AssignedPullRequestRef(
            DefaultConfig.OrganizationUrl,
            DefaultConfig.ProjectId,
            "feature-repo",
            55,
            2,
            PrTitle: "Add feature X",
            RepositoryName: "feature-repo",
            SourceBranch: "feature/add-x",
            TargetBranch: "main");

        this._prFetcher.GetAssignedOpenPullRequestsAsync(DefaultConfig).ReturnsForAnyArgs([prWithContext]);
        this._jobs.FindActiveJob(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>())
            .Returns((ReviewJob?)null);

        // Act
        await this._sut.CrawlAsync();

        // Assert: UpdatePrContextAsync was called with the PR context fields
        await this._jobs.Received(1).UpdatePrContextAsync(
            Arg.Any<Guid>(),
            "Add feature X",
            "feature-repo",
            "feature/add-x",
            "main",
            Arg.Any<CancellationToken>());
    }
}

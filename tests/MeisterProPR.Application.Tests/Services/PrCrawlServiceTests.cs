// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Crawling.Execution.Models;
using MeisterProPR.Application.Features.Crawling.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Services;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Application.Tests.Services;

/// <summary>Unit tests for <see cref="PrCrawlService" /> using NSubstitute.</summary>
public sealed class PrCrawlServiceTests
{
    private static readonly Guid DefaultReviewerId = Guid.NewGuid();

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

    private readonly IClientRegistry _clientRegistry = Substitute.For<IClientRegistry>();

    private readonly ICrawlConfigurationRepository _crawlConfigs = Substitute.For<ICrawlConfigurationRepository>();
    private readonly IJobRepository _jobs = Substitute.For<IJobRepository>();

    private readonly IAssignedReviewDiscoveryService _prFetcher = Substitute.For<IAssignedReviewDiscoveryService>();

    private readonly IProviderActivationService _providerActivationService =
        Substitute.For<IProviderActivationService>();

    private readonly IReviewPrScanRepository _prScanRepository = Substitute.For<IReviewPrScanRepository>();
    private readonly IPrStatusFetcher _statusFetcher = Substitute.For<IPrStatusFetcher>();
    private readonly PrCrawlService _sut;
    private readonly IThreadMemoryService _threadMemoryService = Substitute.For<IThreadMemoryService>();
    private readonly IReviewerThreadStatusFetcher _threadStatusFetcher = Substitute.For<IReviewerThreadStatusFetcher>();

    public PrCrawlServiceTests()
    {
        this._sut = new PrCrawlService(
            this._crawlConfigs,
            this._prFetcher,
            this._jobs,
            this._statusFetcher,
            NullLogger<PrCrawlService>.Instance,
            providerActivationService: this._providerActivationService,
            clientRegistry: this._clientRegistry);

        this._providerActivationService.IsEnabledAsync(Arg.Any<ScmProvider>(), Arg.Any<CancellationToken>())
            .Returns(true);
        this._clientRegistry.GetReviewerIdentityAsync(DefaultConfig.ClientId, Arg.Any<ProviderHostRef>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReviewerIdentity(
                    new ProviderHostRef(DefaultConfig.Provider, DefaultConfig.ProviderScopePath),
                    DefaultReviewerId.ToString("D"),
                    "review-bot",
                    "Review Bot",
                    true));
    }

    private static AssignedCodeReviewRef MakePr(
        int prId = 1,
        int iterationId = 1,
        CrawlConfigurationDto? config = null,
        string repositoryId = "repo-1",
        string? reviewTitle = null,
        string? repositoryDisplayName = null,
        string? sourceBranch = null,
        string? targetBranch = null,
        ReviewRevision? reviewRevision = null)
    {
        var effectiveConfig = config ?? DefaultConfig;
        var host = new ProviderHostRef(ScmProvider.AzureDevOps, effectiveConfig.ProviderScopePath);
        var repository = new RepositoryRef(
            host,
            repositoryId,
            effectiveConfig.ProviderProjectKey,
            effectiveConfig.ProviderProjectKey);
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, prId.ToString(), prId);

        return new AssignedCodeReviewRef(
            host,
            repository,
            review,
            iterationId,
            reviewTitle,
            repositoryDisplayName,
            sourceBranch,
            targetBranch,
            reviewRevision);
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

    private static ReviewerIdentity MakeConfiguredReviewer(CrawlConfigurationDto config, Guid? reviewerId = null)
    {
        var host = new ProviderHostRef(config.Provider, config.ProviderScopePath);

        return new ReviewerIdentity(
            host,
            (reviewerId ?? Guid.NewGuid()).ToString(),
            "review-bot",
            "Review Bot",
            true);
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
            this._prScanRepository,
            providerActivationService: this._providerActivationService,
            clientRegistry: this._clientRegistry);
    }

    private PrCrawlService CreateSutWithSharedSynchronizationService(IPullRequestSynchronizationService synchronizationService)
    {
        return new PrCrawlService(
            this._crawlConfigs,
            this._prFetcher,
            this._jobs,
            this._statusFetcher,
            NullLogger<PrCrawlService>.Instance,
            pullRequestSynchronizationService: synchronizationService,
            providerActivationService: this._providerActivationService,
            clientRegistry: this._clientRegistry);
    }

    [Fact]
    public async Task CrawlAsync_DisabledProvider_DoesNotQueryAssignedReviews()
    {
        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        this._providerActivationService.IsEnabledAsync(DefaultConfig.Provider, Arg.Any<CancellationToken>())
            .Returns(false);

        await this._sut.CrawlAsync();

        await this._prFetcher.DidNotReceive()
            .ListAssignedOpenReviewsAsync(Arg.Any<CrawlConfigurationDto>(), Arg.Any<CancellationToken>());
        await this._jobs.DidNotReceive().AddAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CrawlAsync_AssignedPrWithExistingActiveJob_DoesNotAddJob()
    {
        // Arrange
        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        var pr = MakePr(99);
        this._prFetcher.ListAssignedOpenReviewsAsync(DefaultConfig).ReturnsForAnyArgs([pr]);

        // FindActiveJob returns an existing job (Pending/Processing/Completed)
        var existingJob = new ReviewJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            DefaultConfig.ProviderScopePath,
            DefaultConfig.ProviderProjectKey,
            pr.Repository.ExternalRepositoryId,
            pr.CodeReview.Number,
            pr.RevisionId);
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
        this._prFetcher.ListAssignedOpenReviewsAsync(DefaultConfig).ReturnsForAnyArgs([pr]);
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
        this._prFetcher.ListAssignedOpenReviewsAsync(DefaultConfig).ReturnsForAnyArgs([pr]);
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
            ReviewTemperature = 0.3f,
        };

        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([config]);
        var pr = MakePr(48);
        this._prFetcher.ListAssignedOpenReviewsAsync(config).ReturnsForAnyArgs([pr]);
        this._jobs.FindActiveJob(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>())
            .Returns((ReviewJob?)null);

        await this._sut.CrawlAsync();

        await this._jobs.Received(1)
            .AddAsync(
                Arg.Is<ReviewJob>(job =>
                    job.PullRequestId == pr.CodeReview.Number &&
                    job.ProCursorSourceScopeMode == ProCursorSourceScopeMode.SelectedSources &&
                    job.ProCursorSourceIds.SequenceEqual(new[] { sourceId }) &&
                    job.ReviewTemperature == 0.3f));
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
        this._prFetcher.ListAssignedOpenReviewsAsync(config).ReturnsForAnyArgs([MakePr(49)]);
        this._jobs.FindActiveJob(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>())
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
        var pr = MakePr(42, 3);
        this._prFetcher.ListAssignedOpenReviewsAsync(DefaultConfig).ReturnsForAnyArgs([pr]);
        this._jobs.FindActiveJob(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>())
            .Returns((ReviewJob?)null);
        this._prScanRepository.GetAsync(
                DefaultConfig.ClientId,
                pr.Repository.ExternalRepositoryId,
                pr.CodeReview.Number,
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<ReviewPrScan?>(
                    MakeScan(
                        pr.CodeReview.Number,
                        pr.RevisionId,
                        new ReviewPrScanThread { ThreadId = 7, LastSeenReplyCount = 1, LastSeenStatus = "Active" })));
        this._threadStatusFetcher.GetReviewerThreadStatusesAsync(
                DefaultConfig.ProviderScopePath,
                pr.Repository.ProjectPath,
                pr.Repository.ExternalRepositoryId,
                pr.CodeReview.Number,
                DefaultReviewerId,
                DefaultConfig.ClientId,
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<IReadOnlyList<PrThreadStatusEntry>>(
                [
                    new PrThreadStatusEntry(7, "Active", "/src/file.ts", "Bot: initial comment\nUser: follow-up", 1),
                ]));

        // Act
        await sut.CrawlAsync();

        // Assert
        await this._jobs.DidNotReceive().AddAsync(Arg.Any<ReviewJob>());
    }

    [Fact]
    public async Task CrawlAsync_SameIterationWithoutLegacyReviewerId_UsesConfiguredProviderReviewerIdentity()
    {
        var sut = this.CreateSutWithScanDependencies();
        var providerReviewerId = Guid.NewGuid();
        var config = DefaultConfig;
        var reviewer = MakeConfiguredReviewer(config, providerReviewerId);
        var pr = MakePr(142, 3, config);

        this._clientRegistry.GetReviewerIdentityAsync(
                config.ClientId,
                Arg.Any<ProviderHostRef>(),
                Arg.Any<CancellationToken>())
            .Returns(reviewer);
        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([config]);
        this._prFetcher.ListAssignedOpenReviewsAsync(config).ReturnsForAnyArgs([pr]);
        this._jobs.FindActiveJob(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>())
            .Returns((ReviewJob?)null);
        this._prScanRepository.GetAsync(
                config.ClientId,
                pr.Repository.ExternalRepositoryId,
                pr.CodeReview.Number,
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<ReviewPrScan?>(
                    MakeScan(
                        pr.CodeReview.Number,
                        pr.RevisionId,
                        new ReviewPrScanThread { ThreadId = 7, LastSeenReplyCount = 1, LastSeenStatus = "Active" })));
        this._threadStatusFetcher.GetReviewerThreadStatusesAsync(
                config.ProviderScopePath,
                pr.Repository.ProjectPath,
                pr.Repository.ExternalRepositoryId,
                pr.CodeReview.Number,
                providerReviewerId,
                config.ClientId,
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<IReadOnlyList<PrThreadStatusEntry>>(
                [
                    new PrThreadStatusEntry(7, "Active", "/src/file.ts", "Bot: initial comment\nUser: follow-up", 1),
                ]));

        await sut.CrawlAsync();

        await this._jobs.DidNotReceive().AddAsync(Arg.Any<ReviewJob>());
        await this._threadStatusFetcher.Received(2)
            .GetReviewerThreadStatusesAsync(
                config.ProviderScopePath,
                pr.Repository.ProjectPath,
                pr.Repository.ExternalRepositoryId,
                pr.CodeReview.Number,
                providerReviewerId,
                config.ClientId,
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CrawlAsync_SameIterationWithoutNewReplies_DoesNotRecordNoOpActivity()
    {
        var sut = this.CreateSutWithScanDependencies();
        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        var pr = MakePr(42, 3);
        this._prFetcher.ListAssignedOpenReviewsAsync(DefaultConfig).ReturnsForAnyArgs([pr]);
        this._jobs.FindActiveJob(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>())
            .Returns((ReviewJob?)null);
        this._prScanRepository.GetAsync(
                DefaultConfig.ClientId,
                pr.Repository.ExternalRepositoryId,
                pr.CodeReview.Number,
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<ReviewPrScan?>(
                    MakeScan(
                        pr.CodeReview.Number,
                        pr.RevisionId,
                        new ReviewPrScanThread { ThreadId = 7, LastSeenReplyCount = 1, LastSeenStatus = "Active" })));
        this._threadStatusFetcher.GetReviewerThreadStatusesAsync(
                DefaultConfig.ProviderScopePath,
                pr.Repository.ProjectPath,
                pr.Repository.ExternalRepositoryId,
                pr.CodeReview.Number,
                DefaultReviewerId,
                DefaultConfig.ClientId,
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<IReadOnlyList<PrThreadStatusEntry>>(
                [
                    new PrThreadStatusEntry(7, "Active", "/src/file.ts", "Bot: initial comment\nUser: follow-up", 1),
                ]));

        await sut.CrawlAsync();

        await this._threadMemoryService.DidNotReceive()
            .RecordNoOpAsync(
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
    public async Task CrawlAsync_MatchingProviderRevisionWithoutNewReplies_DoesNotAddJob()
    {
        var sut = this.CreateSutWithScanDependencies();
        var reviewRevision = new ReviewRevision("head-sha", "base-sha", "start-sha", "revision-1", "patch-1");
        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        var pr = MakePr(42, 3, reviewRevision: reviewRevision);
        this._prFetcher.ListAssignedOpenReviewsAsync(DefaultConfig).ReturnsForAnyArgs([pr]);
        this._jobs.FindActiveJob(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>())
            .Returns((ReviewJob?)null);
        this._prScanRepository.GetAsync(
                DefaultConfig.ClientId,
                pr.Repository.ExternalRepositoryId,
                pr.CodeReview.Number,
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<ReviewPrScan?>(
                    new ReviewPrScan(
                        Guid.NewGuid(),
                        DefaultConfig.ClientId,
                        pr.Repository.ExternalRepositoryId,
                        pr.CodeReview.Number,
                        "revision-1")));
        this._threadStatusFetcher.GetReviewerThreadStatusesAsync(
                DefaultConfig.ProviderScopePath,
                pr.Repository.ProjectPath,
                pr.Repository.ExternalRepositoryId,
                pr.CodeReview.Number,
                DefaultReviewerId,
                DefaultConfig.ClientId,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PrThreadStatusEntry>>([]));

        await sut.CrawlAsync();

        await this._jobs.DidNotReceive().AddAsync(Arg.Any<ReviewJob>());
    }

    [Fact]
    public async Task CrawlAsync_AssignedPrWithReviewRevision_SnapshotsRevisionOnQueuedJob()
    {
        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        var reviewRevision = new ReviewRevision("head-sha", "base-sha", "start-sha", "revision-1", "patch-1");
        var pr = MakePr(42, 3, reviewRevision: reviewRevision);
        this._prFetcher.ListAssignedOpenReviewsAsync(DefaultConfig).ReturnsForAnyArgs([pr]);
        this._jobs.FindActiveJob(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>())
            .Returns((ReviewJob?)null);

        await this._sut.CrawlAsync();

        await this._jobs.Received(1)
            .AddAsync(
                Arg.Is<ReviewJob>(job =>
                    job.ProviderRevisionId == "revision-1" &&
                    job.ReviewPatchIdentity == "patch-1" &&
                    job.RevisionHeadSha == "head-sha" &&
                    job.RevisionBaseSha == "base-sha"));
    }

    [Fact]
    public async Task CrawlAsync_SameIterationWithNewReplies_AddsJob()
    {
        // Arrange
        var sut = this.CreateSutWithScanDependencies();
        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        var pr = MakePr(43, 5);
        this._prFetcher.ListAssignedOpenReviewsAsync(DefaultConfig).ReturnsForAnyArgs([pr]);
        this._jobs.FindActiveJob(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>())
            .Returns((ReviewJob?)null);
        this._prScanRepository.GetAsync(
                DefaultConfig.ClientId,
                pr.Repository.ExternalRepositoryId,
                pr.CodeReview.Number,
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<ReviewPrScan?>(
                    MakeScan(
                        pr.CodeReview.Number,
                        pr.RevisionId,
                        new ReviewPrScanThread { ThreadId = 8, LastSeenReplyCount = 0, LastSeenStatus = "Active" })));
        this._threadStatusFetcher.GetReviewerThreadStatusesAsync(
                DefaultConfig.ProviderScopePath,
                pr.Repository.ProjectPath,
                pr.Repository.ExternalRepositoryId,
                pr.CodeReview.Number,
                DefaultReviewerId,
                DefaultConfig.ClientId,
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<IReadOnlyList<PrThreadStatusEntry>>(
                [
                    new PrThreadStatusEntry(8, "Active", "/src/file.ts", "Bot: initial comment\nUser: new reply", 1),
                ]));

        // Act
        await sut.CrawlAsync();

        // Assert
        await this._jobs.Received(1)
            .AddAsync(Arg.Is<ReviewJob>(j => j.PullRequestId == pr.CodeReview.Number && j.IterationId == pr.RevisionId));
    }

    [Fact]
    public async Task CrawlAsync_NewIterationStillAddsJob_WhenScanExists()
    {
        // Arrange
        var sut = this.CreateSutWithScanDependencies();
        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        var pr = MakePr(44, 6);
        this._prFetcher.ListAssignedOpenReviewsAsync(DefaultConfig).ReturnsForAnyArgs([pr]);
        this._jobs.FindActiveJob(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>())
            .Returns((ReviewJob?)null);
        this._prScanRepository.GetAsync(
                DefaultConfig.ClientId,
                pr.Repository.ExternalRepositoryId,
                pr.CodeReview.Number,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewPrScan?>(MakeScan(pr.CodeReview.Number, 5)));

        // Act
        await sut.CrawlAsync();

        // Assert
        await this._jobs.Received(1)
            .AddAsync(Arg.Is<ReviewJob>(j => j.PullRequestId == pr.CodeReview.Number && j.IterationId == pr.RevisionId));
    }

    [Fact]
    public async Task CrawlAsync_MissingScanStillAddsJob_ForRecovery()
    {
        // Arrange
        var sut = this.CreateSutWithScanDependencies();
        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        var pr = MakePr(45, 2);
        this._prFetcher.ListAssignedOpenReviewsAsync(DefaultConfig).ReturnsForAnyArgs([pr]);
        this._jobs.FindActiveJob(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>())
            .Returns((ReviewJob?)null);
        this._prScanRepository.GetAsync(
                DefaultConfig.ClientId,
                pr.Repository.ExternalRepositoryId,
                pr.CodeReview.Number,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewPrScan?>(null));

        // Act
        await sut.CrawlAsync();

        // Assert
        await this._jobs.Received(1)
            .AddAsync(Arg.Is<ReviewJob>(j => j.PullRequestId == pr.CodeReview.Number && j.IterationId == pr.RevisionId));
    }

    [Fact]
    public async Task CrawlAsync_MissingScanButCompletedSameIterationExists_DoesNotAddJob()
    {
        // Arrange
        var sut = this.CreateSutWithScanDependencies();
        var pr = MakePr(46, 2);
        var completedJob = new ReviewJob(
            Guid.NewGuid(),
            DefaultConfig.ClientId,
            DefaultConfig.ProviderScopePath,
            pr.Repository.ProjectPath,
            pr.Repository.ExternalRepositoryId,
            pr.CodeReview.Number,
            pr.RevisionId)
        {
            Status = JobStatus.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
        };

        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        this._prFetcher.ListAssignedOpenReviewsAsync(DefaultConfig).ReturnsForAnyArgs([pr]);
        this._jobs.FindActiveJob(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>())
            .Returns((ReviewJob?)null);
        this._jobs.FindCompletedJob(
                DefaultConfig.ProviderScopePath,
                pr.Repository.ProjectPath,
                pr.Repository.ExternalRepositoryId,
                pr.CodeReview.Number,
                pr.RevisionId)
            .Returns(completedJob);
        this._prScanRepository.GetAsync(
                DefaultConfig.ClientId,
                pr.Repository.ExternalRepositoryId,
                pr.CodeReview.Number,
                Arg.Any<CancellationToken>())
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
        var pr = MakePr(47, 5);
        var completedJob = new ReviewJob(
            Guid.NewGuid(),
            DefaultConfig.ClientId,
            DefaultConfig.ProviderScopePath,
            pr.Repository.ProjectPath,
            pr.Repository.ExternalRepositoryId,
            pr.CodeReview.Number,
            pr.RevisionId)
        {
            Status = JobStatus.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
        };

        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        this._prFetcher.ListAssignedOpenReviewsAsync(DefaultConfig).ReturnsForAnyArgs([pr]);
        this._jobs.FindActiveJob(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>())
            .Returns((ReviewJob?)null);
        this._jobs.FindCompletedJob(
                DefaultConfig.ProviderScopePath,
                pr.Repository.ProjectPath,
                pr.Repository.ExternalRepositoryId,
                pr.CodeReview.Number,
                pr.RevisionId)
            .Returns(completedJob);
        this._prScanRepository.GetAsync(
                DefaultConfig.ClientId,
                pr.Repository.ExternalRepositoryId,
                pr.CodeReview.Number,
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<ReviewPrScan?>(
                    MakeScan(
                        pr.CodeReview.Number,
                        pr.RevisionId,
                        new ReviewPrScanThread { ThreadId = 9, LastSeenReplyCount = 0, LastSeenStatus = "Active" })));
        this._threadStatusFetcher.GetReviewerThreadStatusesAsync(
                DefaultConfig.ProviderScopePath,
                pr.Repository.ProjectPath,
                pr.Repository.ExternalRepositoryId,
                pr.CodeReview.Number,
                DefaultReviewerId,
                DefaultConfig.ClientId,
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<IReadOnlyList<PrThreadStatusEntry>>(
                [
                    new PrThreadStatusEntry(9, "Active", "/src/file.ts", "Bot: initial comment\nUser: new reply", 1),
                ]));

        // Act
        await sut.CrawlAsync();

        // Assert
        await this._jobs.Received(1)
            .AddAsync(Arg.Is<ReviewJob>(j => j.PullRequestId == pr.CodeReview.Number && j.IterationId == pr.RevisionId));
    }

    [Fact]
    public async Task CrawlAsync_FetchThrows_SkipsConfigAndContinues()
    {
        // Arrange: one config throws, another succeeds
        var config2 = DefaultConfig with { Id = Guid.NewGuid(), ProviderProjectKey = "proj-ok" };
        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig, config2]);

        // First config throws (faulted Task)
        this._prFetcher.ListAssignedOpenReviewsAsync(
                Arg.Is<CrawlConfigurationDto>(c => c.ProviderProjectKey == DefaultConfig.ProviderProjectKey),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<AssignedCodeReviewRef>>(new Exception("ADO error")));

        // Second config succeeds
        this._prFetcher.ListAssignedOpenReviewsAsync(
                Arg.Is<CrawlConfigurationDto>(c => c.ProviderProjectKey == config2.ProviderProjectKey),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AssignedCodeReviewRef>>([MakePr(55)]));

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
        var config2 = DefaultConfig with { Id = Guid.NewGuid(), ProviderProjectKey = "proj-2" };
        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig, config2]);
        var pr1 = MakePr(10);
        var pr2 = MakePr(20, 1, config2, "repo-2");
        this._prFetcher.ListAssignedOpenReviewsAsync(DefaultConfig).ReturnsForAnyArgs([pr1]);
        this._prFetcher.ListAssignedOpenReviewsAsync(config2).ReturnsForAnyArgs([pr2]);
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

    [Fact]
    public async Task CrawlAsync_SharedSynchronizationThrowsForOnePr_ContinuesProcessingRemainingPrs()
    {
        var synchronizationService = Substitute.For<IPullRequestSynchronizationService>();
        synchronizationService.SynchronizeAsync(
                Arg.Any<PullRequestSynchronizationRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.ArgAt<PullRequestSynchronizationRequest>(0);

                if (request.PullRequestId == 1)
                {
                    return Task.FromException<PullRequestSynchronizationOutcome>(new InvalidOperationException("boom"));
                }

                return Task.FromResult(
                    new PullRequestSynchronizationOutcome(
                        PullRequestSynchronizationReviewDecision.None,
                        PullRequestSynchronizationLifecycleDecision.None,
                        []));
            });

        var sut = this.CreateSutWithSharedSynchronizationService(synchronizationService);
        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        this._prFetcher.ListAssignedOpenReviewsAsync(DefaultConfig).ReturnsForAnyArgs([MakePr(), MakePr(2)]);
        this._jobs.GetActiveJobsForConfigAsync(
                DefaultConfig.ProviderScopePath,
                DefaultConfig.ProviderProjectKey,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReviewJob>>([]));

        await sut.CrawlAsync();

        await synchronizationService.Received(2)
            .SynchronizeAsync(Arg.Any<PullRequestSynchronizationRequest>(), Arg.Any<CancellationToken>());
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
            DefaultConfig.ProviderScopePath,
            DefaultConfig.ProviderProjectKey,
            "repo-1",
            99,
            1);
        // discovered list has only PR 101, not 99
        this._prFetcher.ListAssignedOpenReviewsAsync(DefaultConfig, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AssignedCodeReviewRef>>([MakePr(101)]));
        this._jobs.FindActiveJob(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>())
            .Returns((ReviewJob?)null);
        // active jobs for the config include the orphan
        this._jobs.GetActiveJobsForConfigAsync(
                DefaultConfig.ProviderScopePath,
                DefaultConfig.ProviderProjectKey,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReviewJob>>([orphanJob]));
        // ADO reports the PR is Abandoned
        this._statusFetcher.GetStatusAsync(
                DefaultConfig.ProviderScopePath,
                DefaultConfig.ProviderProjectKey,
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
            DefaultConfig.ProviderScopePath,
            DefaultConfig.ProviderProjectKey,
            "repo-1",
            99,
            1);
        this._prFetcher.ListAssignedOpenReviewsAsync(DefaultConfig, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AssignedCodeReviewRef>>([MakePr(101)]));
        this._jobs.FindActiveJob(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>())
            .Returns((ReviewJob?)null);
        this._jobs.GetActiveJobsForConfigAsync(
                DefaultConfig.ProviderScopePath,
                DefaultConfig.ProviderProjectKey,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReviewJob>>([orphanJob]));
        // ADO says still Active (reviewer check returned nothing; missing from assign list is a race)
        this._statusFetcher.GetStatusAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
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
        this._prFetcher.ListAssignedOpenReviewsAsync(DefaultConfig, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AssignedCodeReviewRef>>([]));
        this._jobs.FindActiveJob(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>())
            .Returns((ReviewJob?)null);
        // GetActiveJobsForConfigAsync returns empty (Completed jobs are not "active")
        this._jobs.GetActiveJobsForConfigAsync(
                DefaultConfig.ProviderScopePath,
                DefaultConfig.ProviderProjectKey,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReviewJob>>([]));

        // Act
        await this._sut.CrawlAsync();

        // Assert: status fetcher never called; no cancellation
        await this._statusFetcher.DidNotReceiveWithAnyArgs()
            .GetStatusAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>());
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
            DefaultConfig.ProviderScopePath,
            DefaultConfig.ProviderProjectKey,
            "repo-1",
            99,
            1);
        this._prFetcher.ListAssignedOpenReviewsAsync(DefaultConfig, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AssignedCodeReviewRef>>([MakePr(42)]));
        this._jobs.FindActiveJob(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>())
            .Returns((ReviewJob?)null);
        this._jobs.GetActiveJobsForConfigAsync(
                DefaultConfig.ProviderScopePath,
                DefaultConfig.ProviderProjectKey,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReviewJob>>([orphanJob]));
        // Note: IPrStatusFetcher is expected to swallow exceptions itself (fail-safe contract).
        // So even if the underlying ADO call throws, the interface implementation returns Active.
        // This test verifies no cancellation happens when the fetcher returns Active.
        this._statusFetcher.GetStatusAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PrStatus.Active));

        // Act — must not throw
        await this._sut.CrawlAsync();

        // Assert: no cancellation
        await this._jobs.DidNotReceive().SetCancelledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // T038 — verify that PrCrawlService creates jobs only for reviews returned by the discovery service.
    // Filtering is delegated to IAssignedReviewDiscoveryService; PrCrawlService just processes the list.

    [Fact]
    public async Task CrawlAsync_FetcherReturnsFilteredPrs_CreatesJobsOnlyForReturnedPrs()
    {
        // Arrange: fetcher returns only 1 of 2 discovered PRs (simulates repo/branch filter applied in fetcher)
        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);

        var filteredPr = MakePr(100);
        // Fetcher returns only the filtered PR (non-matching ones are excluded by the fetcher)
        this._prFetcher.ListAssignedOpenReviewsAsync(DefaultConfig).ReturnsForAnyArgs([filteredPr]);
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
        // T038/T047: PrCrawlService calls UpdatePrContextAsync when the AssignedCodeReviewRef
        // has PR context fields populated.
        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);

        var prWithContext = MakePr(
            55,
            2,
            DefaultConfig,
            "feature-repo",
            "Add feature X",
            "feature-repo",
            "feature/add-x",
            "main");

        this._prFetcher.ListAssignedOpenReviewsAsync(DefaultConfig).ReturnsForAnyArgs([prWithContext]);
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
        await this._jobs.Received(1)
            .UpdatePrContextAsync(
                Arg.Any<Guid>(),
                "Add feature X",
                "feature-repo",
                "feature/add-x",
                "main",
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CrawlAsync_WithSharedSynchronizationService_ForwardsAssignedPullRequestToSharedSeam()
    {
        var synchronizationService = Substitute.For<IPullRequestSynchronizationService>();
        var sut = this.CreateSutWithSharedSynchronizationService(synchronizationService);
        var sourceId = Guid.NewGuid();
        var config = DefaultConfig with
        {
            ProCursorSourceScopeMode = ProCursorSourceScopeMode.SelectedSources,
            ProCursorSourceIds = [sourceId],
            InvalidProCursorSourceIds = [],
        };
        var pr = MakePr(
            42,
            7,
            config,
            "repo-1",
            "Add webhook parity",
            "repo-display",
            "feature/parity",
            "main");

        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([config]);
        this._prFetcher.ListAssignedOpenReviewsAsync(config, Arg.Any<CancellationToken>())
            .Returns([pr]);
        this._jobs.GetActiveJobsForConfigAsync(
                config.ProviderScopePath,
                config.ProviderProjectKey,
                Arg.Any<CancellationToken>())
            .Returns([]);
        synchronizationService.SynchronizeAsync(
                Arg.Any<PullRequestSynchronizationRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new PullRequestSynchronizationOutcome(
                    PullRequestSynchronizationReviewDecision.Submitted,
                    PullRequestSynchronizationLifecycleDecision.None,
                    ["Submitted review intake job for PR #42 at iteration 7 via crawl discovery."]));

        await sut.CrawlAsync();

        await synchronizationService.Received(1)
            .SynchronizeAsync(
                Arg.Is<PullRequestSynchronizationRequest>(request =>
                    request.ActivationSource == PullRequestActivationSource.Crawl &&
                    request.SummaryLabel == "crawl discovery" &&
                    request.ClientId == config.ClientId &&
                    request.ProviderScopePath == config.ProviderScopePath &&
                    request.ProviderProjectKey == config.ProviderProjectKey &&
                    request.RepositoryId == pr.Repository.ExternalRepositoryId &&
                    request.PullRequestId == pr.CodeReview.Number &&
                    request.PullRequestStatus == PrStatus.Active &&
                    request.CandidateIterationId == pr.RevisionId &&
                    request.RequestedReviewerIdentity != null &&
                    request.RequestedReviewerIdentity.ExternalUserId == DefaultReviewerId.ToString("D") &&
                    request.PrTitle == pr.ReviewTitle &&
                    request.RepositoryName == pr.RepositoryDisplayName &&
                    request.SourceBranch == pr.SourceBranch &&
                    request.TargetBranch == pr.TargetBranch &&
                    request.ProCursorSourceScopeMode == ProCursorSourceScopeMode.SelectedSources &&
                    request.ProCursorSourceIds.SequenceEqual(new[] { sourceId })),
                Arg.Any<CancellationToken>());
        await this._jobs.DidNotReceive().AddAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CrawlAsync_WithSharedSynchronizationService_ForwardsConfiguredProviderReviewer_WhenLegacyReviewerIdIsMissing()
    {
        var synchronizationService = Substitute.For<IPullRequestSynchronizationService>();
        var sut = this.CreateSutWithSharedSynchronizationService(synchronizationService);
        var providerReviewerId = Guid.NewGuid();
        var config = DefaultConfig;
        var reviewer = MakeConfiguredReviewer(config, providerReviewerId);
        var pr = MakePr(
            43,
            8,
            config,
            "repo-1",
            "Use provider reviewer identity",
            "repo-display",
            "feature/provider-reviewer",
            "main");

        this._clientRegistry.GetReviewerIdentityAsync(
                config.ClientId,
                Arg.Any<ProviderHostRef>(),
                Arg.Any<CancellationToken>())
            .Returns(reviewer);
        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([config]);
        this._prFetcher.ListAssignedOpenReviewsAsync(config, Arg.Any<CancellationToken>())
            .Returns([pr]);
        this._jobs.GetActiveJobsForConfigAsync(
                config.ProviderScopePath,
                config.ProviderProjectKey,
                Arg.Any<CancellationToken>())
            .Returns([]);
        synchronizationService.SynchronizeAsync(
                Arg.Any<PullRequestSynchronizationRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new PullRequestSynchronizationOutcome(
                    PullRequestSynchronizationReviewDecision.Submitted,
                    PullRequestSynchronizationLifecycleDecision.None,
                    ["Submitted review intake job for PR #43 at iteration 8 via crawl discovery."]));

        await sut.CrawlAsync();

        await synchronizationService.Received(1)
            .SynchronizeAsync(
                Arg.Is<PullRequestSynchronizationRequest>(request =>
                    request.PullRequestId == pr.CodeReview.Number &&
                    request.RequestedReviewerIdentity == reviewer &&
                    request.RequestedReviewerIdentity.ExternalUserId == providerReviewerId.ToString("D")),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CrawlAsync_WithSharedSynchronizationService_ForwardsOrphanedJobLifecycleToSharedSeam()
    {
        var synchronizationService = Substitute.For<IPullRequestSynchronizationService>();
        var sut = this.CreateSutWithSharedSynchronizationService(synchronizationService);
        var orphanJob = new ReviewJob(
            Guid.NewGuid(),
            DefaultConfig.ClientId,
            DefaultConfig.ProviderScopePath,
            DefaultConfig.ProviderProjectKey,
            "repo-1",
            99,
            3);

        this._crawlConfigs.GetAllActiveAsync().ReturnsForAnyArgs([DefaultConfig]);
        this._prFetcher.ListAssignedOpenReviewsAsync(DefaultConfig, Arg.Any<CancellationToken>())
            .Returns([MakePr(42)]);
        this._jobs.GetActiveJobsForConfigAsync(
                DefaultConfig.ProviderScopePath,
                DefaultConfig.ProviderProjectKey,
                Arg.Any<CancellationToken>())
            .Returns([orphanJob]);
        this._statusFetcher.GetStatusAsync(
                DefaultConfig.ProviderScopePath,
                DefaultConfig.ProviderProjectKey,
                orphanJob.RepositoryId,
                orphanJob.PullRequestId,
                DefaultConfig.ClientId,
                Arg.Any<CancellationToken>())
            .Returns(PrStatus.Abandoned);
        synchronizationService.SynchronizeAsync(
                Arg.Any<PullRequestSynchronizationRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new PullRequestSynchronizationOutcome(
                    PullRequestSynchronizationReviewDecision.None,
                    PullRequestSynchronizationLifecycleDecision.CancelledActiveJobs,
                    ["Cancelled 1 active review job(s) for PR #99 because the pull request is abandoned."]));

        await sut.CrawlAsync();

        await synchronizationService.Received(1)
            .SynchronizeAsync(
                Arg.Is<PullRequestSynchronizationRequest>(request =>
                    request.ActivationSource == PullRequestActivationSource.Crawl &&
                    request.SummaryLabel == "crawl disappearance" &&
                    request.ClientId == DefaultConfig.ClientId &&
                    request.ProviderScopePath == DefaultConfig.ProviderScopePath &&
                    request.ProviderProjectKey == DefaultConfig.ProviderProjectKey &&
                    request.RepositoryId == orphanJob.RepositoryId &&
                    request.PullRequestId == orphanJob.PullRequestId &&
                    request.PullRequestStatus == PrStatus.Abandoned),
                Arg.Any<CancellationToken>());
        await this._jobs.DidNotReceive().SetCancelledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}

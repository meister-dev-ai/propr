// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Events;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.Crawling.Execution;

public sealed class PullRequestSynchronizationServiceTests
{
    private static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ReviewerId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Theory]
    [InlineData(PullRequestActivationSource.Crawl, "crawl discovery")]
    [InlineData(PullRequestActivationSource.Webhook, "pull request updated")]
    public async Task
        SynchronizeAsync_ActivePullRequest_TriggersSameThreadMemoryAndReviewBehavior_ForAnyActivationSource(
            PullRequestActivationSource activationSource,
            string summaryLabel)
    {
        var jobs = Substitute.For<IJobRepository>();
        var iterationResolver = Substitute.For<IPullRequestIterationResolver>();
        var threadStatusFetcher = Substitute.For<IReviewerThreadStatusFetcher>();
        var threadMemoryService = Substitute.For<IThreadMemoryService>();
        var scanRepository = Substitute.For<IReviewPrScanRepository>();
        var clientRegistry = Substitute.For<IClientRegistry>();
        clientRegistry.GetDefaultReviewPipelineProfileIdAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(ReviewPipelineProfileCatalog.FileByFileAssertiveProfileId);

        jobs.FindActiveJob("https://dev.azure.com/org", "project", "repo-1", 42, 7)
            .Returns((ReviewJob?)null);
        jobs.FindCompletedJob("https://dev.azure.com/org", "project", "repo-1", 42, 7)
            .Returns((ReviewJob?)null);
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryAddReviewJobResult(true, null, 0));
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryAddReviewJobResult(true, null, 0));

        var scan = new ReviewPrScan(Guid.NewGuid(), ClientId, "https://provider.example", "project", "repo-1", 42, "7");
        scan.Threads.Add(
            new ReviewPrScanThread
            {
                ReviewPrScanId = scan.Id,
                ThreadId = "17",
                LastSeenReplyCount = 0,
                LastSeenStatus = "Active",
            });

        scanRepository.GetAsync(ClientId, Arg.Any<string>(), Arg.Any<string>(), "repo-1", 42, Arg.Any<CancellationToken>())
            .Returns(scan);
        threadStatusFetcher.GetReviewerThreadStatusesAsync(
                "https://dev.azure.com/org",
                "project",
                "repo-1",
                42,
                Arg.Any<ThreadOwnershipResolver>(),
                ClientId,
                Arg.Any<CancellationToken>())
            .Returns(
            [
                new PrThreadStatusEntry("17", "Fixed", "/src/file.ts", "Bot: comment\nUser: reply", 1),
            ]);

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance,
            iterationResolver,
            threadStatusFetcher,
            threadMemoryService,
            scanRepository,
            clientRegistry);

        var outcome = await sut.SynchronizeAsync(
            CreateRequest(activationSource, summaryLabel) with
            {
                CandidateIterationId = 7,
                RequestedReviewerIdentity = CreateRequestedReviewerIdentity(),
                ReviewTemperature = 0.4f,
            });

        Assert.Equal(PullRequestSynchronizationReviewDecision.Submitted, outcome.ReviewDecision);
        Assert.Equal(PullRequestSynchronizationLifecycleDecision.None, outcome.LifecycleDecision);
        Assert.Contains(
            outcome.ActionSummaries,
            summary => summary.Contains("Submitted review intake job", StringComparison.OrdinalIgnoreCase));

        await jobs.Received(1)
            .TryAddIfNoActiveDuplicateAsync(
                Arg.Is<ReviewJob>(job =>
                    job.ClientId == ClientId &&
                    job.OrganizationUrl == "https://dev.azure.com/org" &&
                    job.ProjectId == "project" &&
                    job.RepositoryId == "repo-1" &&
                    job.PullRequestId == 42 &&
                    job.IterationId == 7 &&
                    job.ReviewTemperature == 0.4f &&
                    job.ReviewPipelineProfileId == ReviewPipelineProfileCatalog.FileByFileAssertiveProfileId),
                Arg.Any<CancellationToken>());
        await threadMemoryService.Received(1)
            .HandleThreadResolvedAsync(
                Arg.Is<ThreadResolvedDomainEvent>(evt =>
                    evt.ClientId == ClientId &&
                    evt.RepositoryId == "repo-1" &&
                    evt.PullRequestId == 42 &&
                    evt.ThreadId == "17"),
                Arg.Any<CancellationToken>());
        await scanRepository.Received(1)
            .SetLastSeenStatusesAsync(
                ClientId,
                Arg.Any<string>(),
                Arg.Any<string>(),
                "repo-1",
                42,
                Arg.Is<IReadOnlyDictionary<string, string?>>(statuses =>
                    statuses.Count == 1 && statuses["17"] == "Fixed"),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SynchronizeAsync_WhenPullRequestBlocked_SkipsReviewSubmission()
    {
        var jobs = Substitute.For<IJobRepository>();
        var blockStore = Substitute.For<IBlockedPullRequestStore>();
        blockStore.IsBlockedAsync(ClientId, "https://dev.azure.com/org", "project", "repo-1", 42, Arg.Any<CancellationToken>())
            .Returns(true);

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance,
            blockedPullRequestStore: blockStore);

        var outcome = await sut.SynchronizeAsync(
            CreateRequest(PullRequestActivationSource.Webhook, "pull request updated") with
            {
                CandidateIterationId = 7,
            });

        Assert.Equal(PullRequestSynchronizationReviewDecision.None, outcome.ReviewDecision);
        await jobs.DidNotReceive().TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
        await blockStore.Received(1)
            .IsBlockedAsync(ClientId, "https://dev.azure.com/org", "project", "repo-1", 42, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(PullRequestActivationSource.Crawl, "crawl disappearance")]
    [InlineData(PullRequestActivationSource.Webhook, "pull request closure")]
    public async Task SynchronizeAsync_ClosedPullRequest_CancelsMatchingJobs_ForAnyActivationSource(
        PullRequestActivationSource activationSource,
        string summaryLabel)
    {
        var jobs = Substitute.For<IJobRepository>();
        var matching = new ReviewJob(Guid.NewGuid(), ClientId, "https://dev.azure.com/org", "project", "repo-1", 42, 6)
        {
            Status = JobStatus.Pending,
        };
        var unrelated = new ReviewJob(Guid.NewGuid(), ClientId, "https://dev.azure.com/org", "project", "repo-1", 84, 6)
        {
            Status = JobStatus.Pending,
        };

        jobs.GetActiveJobsForConfigAsync("https://dev.azure.com/org", "project", Arg.Any<CancellationToken>())
            .Returns([matching, unrelated]);

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance);

        var outcome = await sut.SynchronizeAsync(
            CreateRequest(activationSource, summaryLabel) with
            {
                PullRequestStatus = PrStatus.Abandoned,
            });

        Assert.Equal(PullRequestSynchronizationReviewDecision.None, outcome.ReviewDecision);
        Assert.Equal(PullRequestSynchronizationLifecycleDecision.CancelledActiveJobs, outcome.LifecycleDecision);
        Assert.Contains(
            outcome.ActionSummaries,
            summary => summary.Contains("Cancelled 1 active review job", StringComparison.OrdinalIgnoreCase));

        await jobs.Received(1).SetCancelledAsync(matching.Id, Arg.Any<CancellationToken>());
        await jobs.DidNotReceive().SetCancelledAsync(unrelated.Id, Arg.Any<CancellationToken>());
        await jobs.DidNotReceive().AddAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(PullRequestActivationSource.Crawl, "crawl discovery")]
    [InlineData(PullRequestActivationSource.Webhook, "pull request updated")]
    public async Task SynchronizeAsync_SameIterationWithoutNewReplies_SkipsReview_ForAnyActivationSource(
        PullRequestActivationSource activationSource,
        string summaryLabel)
    {
        var jobs = Substitute.For<IJobRepository>();
        var iterationResolver = Substitute.For<IPullRequestIterationResolver>();
        var threadStatusFetcher = Substitute.For<IReviewerThreadStatusFetcher>();
        var threadMemoryService = Substitute.For<IThreadMemoryService>();
        var scanRepository = Substitute.For<IReviewPrScanRepository>();
        var clientRegistry = Substitute.For<IClientRegistry>();

        jobs.FindActiveJob("https://dev.azure.com/org", "project", "repo-1", 42, 7)
            .Returns((ReviewJob?)null);
        jobs.FindCompletedJob("https://dev.azure.com/org", "project", "repo-1", 42, 7)
            .Returns((ReviewJob?)null);
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryAddReviewJobResult(true, null, 0));

        var scan = new ReviewPrScan(Guid.NewGuid(), ClientId, "https://provider.example", "project", "repo-1", 42, "7");
        scan.Threads.Add(
            new ReviewPrScanThread
            {
                ReviewPrScanId = scan.Id,
                ThreadId = "17",
                LastSeenReplyCount = 1,
                LastSeenStatus = "Active",
            });

        scanRepository.GetAsync(ClientId, Arg.Any<string>(), Arg.Any<string>(), "repo-1", 42, Arg.Any<CancellationToken>())
            .Returns(scan);
        threadStatusFetcher.GetReviewerThreadStatusesAsync(
                "https://dev.azure.com/org",
                "project",
                "repo-1",
                42,
                Arg.Any<ThreadOwnershipResolver>(),
                ClientId,
                Arg.Any<CancellationToken>())
            .Returns(
            [
                new PrThreadStatusEntry("17", "Active", "/src/file.ts", "Bot: comment\nUser: reply", 1),
            ]);

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance,
            iterationResolver,
            threadStatusFetcher,
            threadMemoryService,
            scanRepository);

        var outcome = await sut.SynchronizeAsync(
            CreateRequest(activationSource, summaryLabel) with
            {
                CandidateIterationId = 7,
                RequestedReviewerIdentity = CreateRequestedReviewerIdentity(),
            });

        Assert.Equal(PullRequestSynchronizationReviewDecision.NoReviewChanges, outcome.ReviewDecision);
        Assert.Equal(PullRequestSynchronizationLifecycleDecision.None, outcome.LifecycleDecision);
        Assert.Contains(
            outcome.ActionSummaries,
            summary => summary.Contains("no new changes", StringComparison.OrdinalIgnoreCase));

        await jobs.DidNotReceive().AddAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
        await jobs.DidNotReceive().TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
        await threadMemoryService.DidNotReceive()
            .HandleThreadResolvedAsync(Arg.Any<ThreadResolvedDomainEvent>(), Arg.Any<CancellationToken>());
        await threadMemoryService.DidNotReceive()
            .HandleThreadReopenedAsync(Arg.Any<ThreadReopenedDomainEvent>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(PullRequestActivationSource.Crawl, "crawl discovery")]
    [InlineData(PullRequestActivationSource.Webhook, "pull request updated")]
    public async Task SynchronizeAsync_PriorReviewFailedAtSameRevision_BlocksAutoReview_EvenWithNewThreadReplies(
        PullRequestActivationSource activationSource,
        string summaryLabel)
    {
        var jobs = Substitute.For<IJobRepository>();
        var iterationResolver = Substitute.For<IPullRequestIterationResolver>();
        var threadStatusFetcher = Substitute.For<IReviewerThreadStatusFetcher>();
        var threadMemoryService = Substitute.For<IThreadMemoryService>();
        var scanRepository = Substitute.For<IReviewPrScanRepository>();

        jobs.FindActiveJob("https://dev.azure.com/org", "project", "repo-1", 42, 7)
            .Returns((ReviewJob?)null);
        jobs.FindCompletedJob("https://dev.azure.com/org", "project", "repo-1", 42, 7)
            .Returns((ReviewJob?)null);

        // A prior review for this exact revision already failed.
        var failedJob = new ReviewJob(Guid.NewGuid(), ClientId, "https://dev.azure.com/org", "project", "repo-1", 42, 7)
        {
            Status = JobStatus.Failed,
        };
        jobs.FindFailedJob("https://dev.azure.com/org", "project", "repo-1", 42, 7)
            .Returns(failedJob);

        // A scan with a fresh reviewer reply exists — under the old rules this would re-trigger a review.
        var scan = new ReviewPrScan(Guid.NewGuid(), ClientId, "https://provider.example", "project", "repo-1", 42, "7");
        scan.Threads.Add(
            new ReviewPrScanThread
            {
                ReviewPrScanId = scan.Id,
                ThreadId = "17",
                LastSeenReplyCount = 0,
                LastSeenStatus = "Active",
            });
        scanRepository.GetAsync(ClientId, Arg.Any<string>(), Arg.Any<string>(), "repo-1", 42, Arg.Any<CancellationToken>())
            .Returns(scan);
        threadStatusFetcher.GetReviewerThreadStatusesAsync(
                "https://dev.azure.com/org",
                "project",
                "repo-1",
                42,
                Arg.Any<ThreadOwnershipResolver>(),
                ClientId,
                Arg.Any<CancellationToken>())
            .Returns(
            [
                new PrThreadStatusEntry("17", "Active", "/src/file.ts", "Bot: comment\nUser: new reply", 1),
            ]);

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance,
            iterationResolver,
            threadStatusFetcher,
            threadMemoryService,
            scanRepository);

        var outcome = await sut.SynchronizeAsync(
            CreateRequest(activationSource, summaryLabel) with
            {
                CandidateIterationId = 7,
                RequestedReviewerIdentity = CreateRequestedReviewerIdentity(),
            });

        Assert.Equal(PullRequestSynchronizationReviewDecision.FailedAwaitingRestart, outcome.ReviewDecision);
        Assert.Equal(PullRequestSynchronizationLifecycleDecision.None, outcome.LifecycleDecision);
        Assert.Contains(
            outcome.ActionSummaries,
            summary => summary.Contains("manual restart is required", StringComparison.OrdinalIgnoreCase));
        await jobs.DidNotReceive().AddAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
        await jobs.DidNotReceive().TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SynchronizeAsync_NewIterationAfterPriorFailure_QueuesReviewAgain()
    {
        var jobs = Substitute.For<IJobRepository>();

        // The prior failure was at iteration 7; the PR now advanced to iteration 8 (new commits).
        jobs.FindActiveJob("https://dev.azure.com/org", "project", "repo-1", 42, 8)
            .Returns((ReviewJob?)null);
        jobs.FindCompletedJob("https://dev.azure.com/org", "project", "repo-1", 42, 8)
            .Returns((ReviewJob?)null);
        jobs.FindFailedJob("https://dev.azure.com/org", "project", "repo-1", 42, 8)
            .Returns((ReviewJob?)null);
        jobs.FindFailedJob("https://dev.azure.com/org", "project", "repo-1", 42, 7)
            .Returns(
                new ReviewJob(Guid.NewGuid(), ClientId, "https://dev.azure.com/org", "project", "repo-1", 42, 7)
                {
                    Status = JobStatus.Failed,
                });
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryAddReviewJobResult(true, null, 0));

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance);

        var outcome = await sut.SynchronizeAsync(
            CreateRequest(PullRequestActivationSource.Crawl, "crawl discovery") with
            {
                CandidateIterationId = 8,
            });

        Assert.Equal(PullRequestSynchronizationReviewDecision.Submitted, outcome.ReviewDecision);
        await jobs.Received(1)
            .TryAddIfNoActiveDuplicateAsync(
                Arg.Is<ReviewJob>(job => job.IterationId == 8),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SynchronizeAsync_SelectedSourceScope_WithNullCollections_TreatsThemAsEmpty()
    {
        var jobs = Substitute.For<IJobRepository>();
        jobs.FindActiveJob("https://dev.azure.com/org", "project", "repo-1", 42, 7)
            .Returns((ReviewJob?)null);
        jobs.FindCompletedJob("https://dev.azure.com/org", "project", "repo-1", 42, 7)
            .Returns((ReviewJob?)null);

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance);

        var outcome = await sut.SynchronizeAsync(
            CreateRequest(PullRequestActivationSource.Crawl, "crawl discovery") with
            {
                CandidateIterationId = 7,
                ProCursorSourceScopeMode = ProCursorSourceScopeMode.SelectedSources,
                ProCursorSourceIds = null!,
                InvalidProCursorSourceIds = null!,
            });

        Assert.Equal(PullRequestSynchronizationReviewDecision.EmptySourceScope, outcome.ReviewDecision);
        Assert.Equal(PullRequestSynchronizationLifecycleDecision.None, outcome.LifecycleDecision);
        Assert.Contains(
            outcome.ActionSummaries,
            summary => summary.Contains(
                "selected ProCursor source scope is empty",
                StringComparison.OrdinalIgnoreCase));
        await jobs.DidNotReceive().AddAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
        await jobs.DidNotReceive().TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SynchronizeAsync_WhenAtomicReservationDetectsDuplicate_ReturnsDuplicateOutcome()
    {
        var jobs = Substitute.For<IJobRepository>();
        var duplicateJob = new ReviewJob(Guid.NewGuid(), ClientId, "https://dev.azure.com/org", "project", "repo-1", 42, 7)
        {
            Status = JobStatus.Pending,
        };

        jobs.FindActiveJob("https://dev.azure.com/org", "project", "repo-1", 42, 7)
            .Returns((ReviewJob?)null);
        jobs.FindCompletedJob("https://dev.azure.com/org", "project", "repo-1", 42, 7)
            .Returns((ReviewJob?)null);
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryAddReviewJobResult(false, duplicateJob, 0));

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance);

        var outcome = await sut.SynchronizeAsync(
            CreateRequest(PullRequestActivationSource.Webhook, "pull request updated") with
            {
                CandidateIterationId = 7,
            });

        Assert.Equal(PullRequestSynchronizationReviewDecision.DuplicateActiveJob, outcome.ReviewDecision);
        Assert.Contains(
            outcome.ActionSummaries,
            summary => summary.Contains("Skipped duplicate active job", StringComparison.OrdinalIgnoreCase));
        await jobs.DidNotReceive().AddAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task
        SynchronizeAsync_WithNumericProviderRevisionId_UsesProviderIterationIdInsteadOfSynthesizingOne()
    {
        // Regression: ADO webhooks supply a real iteration id in ReviewRevision.ProviderRevisionId.
        // The synchronization service must trust that value rather than hash it into a synthetic id
        // that downstream provider lookups (GetPullRequestIterationAsync) cannot resolve.
        const int providerIterationId = 7;

        var jobs = Substitute.For<IJobRepository>();
        var iterationResolver = Substitute.For<IPullRequestIterationResolver>();
        jobs.GetActiveJobsForConfigAsync("https://dev.azure.com/org", "project", Arg.Any<CancellationToken>())
            .Returns([]);
        jobs.FindActiveJob("https://dev.azure.com/org", "project", "repo-1", 42, providerIterationId)
            .Returns((ReviewJob?)null);
        jobs.FindCompletedJob("https://dev.azure.com/org", "project", "repo-1", 42, providerIterationId)
            .Returns((ReviewJob?)null);
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryAddReviewJobResult(true, null, 0));

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance,
            iterationResolver);

        var outcome = await sut.SynchronizeAsync(
            CreateRequest(PullRequestActivationSource.Webhook, "pull request created") with
            {
                CandidateIterationId = null,
                ReviewRevision = new ReviewRevision(
                    "head-sha",
                    "base-sha",
                    "base-sha",
                    providerIterationId.ToString(CultureInfo.InvariantCulture),
                    "base-sha...head-sha"),
            });

        Assert.Equal(PullRequestSynchronizationReviewDecision.Submitted, outcome.ReviewDecision);
        await iterationResolver.DidNotReceive()
            .GetLatestIterationIdAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>());
        await jobs.Received(1)
            .TryAddIfNoActiveDuplicateAsync(
                Arg.Is<ReviewJob>(job => job.IterationId == providerIterationId),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task
        SynchronizeAsync_WithNonNumericProviderRevisionId_StillSynthesizesIterationIdWithoutCallingResolver()
    {
        // Providers without numeric iteration ids (GitHub/GitLab/Forgejo) keep the SHA-256 fallback,
        // so we still avoid the resolver and queue a job with a deterministic synthetic id.
        var jobs = Substitute.For<IJobRepository>();
        var iterationResolver = Substitute.For<IPullRequestIterationResolver>();
        jobs.GetActiveJobsForConfigAsync("https://dev.azure.com/org", "project", Arg.Any<CancellationToken>())
            .Returns([]);
        jobs.FindActiveJob(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>())
            .Returns((ReviewJob?)null);
        jobs.FindCompletedJob(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>())
            .Returns((ReviewJob?)null);
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryAddReviewJobResult(true, null, 0));

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance,
            iterationResolver);

        var outcome = await sut.SynchronizeAsync(
            CreateRequest(PullRequestActivationSource.Webhook, "pull request updated") with
            {
                CandidateIterationId = null,
                ReviewRevision = new ReviewRevision(
                    "head-sha",
                    "base-sha",
                    "start-sha",
                    "revision-abc",
                    "patch-1"),
            });

        Assert.Equal(PullRequestSynchronizationReviewDecision.Submitted, outcome.ReviewDecision);
        await iterationResolver.DidNotReceive()
            .GetLatestIterationIdAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>());
        await jobs.Received(1)
            .TryAddIfNoActiveDuplicateAsync(
                Arg.Is<ReviewJob>(job => job.IterationId > 0),
                Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Thread memory reconciliation and the review decision both consume the reviewer's threads. The
    ///     pass asks the provider once and shares the snapshot, which halves the crawl's outbound calls
    ///     for every open pull request on every tick.
    /// </summary>
    [Fact]
    public async Task SynchronizeAsync_ActivePullRequest_FetchesReviewerThreadStatusesOncePerPass()
    {
        var jobs = Substitute.For<IJobRepository>();
        var iterationResolver = Substitute.For<IPullRequestIterationResolver>();
        var threadStatusFetcher = Substitute.For<IReviewerThreadStatusFetcher>();
        var threadMemoryService = Substitute.For<IThreadMemoryService>();
        var scanRepository = Substitute.For<IReviewPrScanRepository>();

        jobs.FindActiveJob("https://dev.azure.com/org", "project", "repo-1", 42, 7).Returns((ReviewJob?)null);
        jobs.FindCompletedJob("https://dev.azure.com/org", "project", "repo-1", 42, 7).Returns((ReviewJob?)null);
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryAddReviewJobResult(true, null, 0));

        var scan = new ReviewPrScan(Guid.NewGuid(), ClientId, "https://provider.example", "project", "repo-1", 42, "7");
        scan.Threads.Add(
            new ReviewPrScanThread
            {
                ReviewPrScanId = scan.Id,
                ThreadId = "17",
                LastSeenReplyCount = 0,
                LastSeenStatus = "Active",
            });

        scanRepository.GetAsync(ClientId, Arg.Any<string>(), Arg.Any<string>(), "repo-1", 42, Arg.Any<CancellationToken>()).Returns(scan);
        threadStatusFetcher.GetReviewerThreadStatusesAsync(
                "https://dev.azure.com/org",
                "project",
                "repo-1",
                42,
                Arg.Any<ThreadOwnershipResolver>(),
                ClientId,
                Arg.Any<CancellationToken>())
            .Returns(
            [
                new PrThreadStatusEntry("17", "Fixed", "/src/file.ts", "Bot: comment\nUser: reply", 1),
            ]);

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance,
            iterationResolver,
            threadStatusFetcher,
            threadMemoryService,
            scanRepository);

        await sut.SynchronizeAsync(
            CreateRequest(PullRequestActivationSource.Crawl, "crawl discovery") with
            {
                CandidateIterationId = 7,
                RequestedReviewerIdentity = CreateRequestedReviewerIdentity(),
            });

        await threadStatusFetcher.Received(1)
            .GetReviewerThreadStatusesAsync(
                "https://dev.azure.com/org",
                "project",
                "repo-1",
                42,
                Arg.Any<ThreadOwnershipResolver>(),
                ClientId,
                Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     An automatic trigger reviews a pull request at the revision it first sees. With nothing on record for the
    ///     pull request there is no earlier increment to defer to, so the review is submitted as before.
    /// </summary>
    [Theory]
    [InlineData(PullRequestActivationSource.Crawl, "crawl discovery")]
    [InlineData(PullRequestActivationSource.Webhook, "pull request updated")]
    public async Task SynchronizeAsync_FirstSightingOfPullRequest_SubmitsReview_ForAnyAutomaticSource(
        PullRequestActivationSource activationSource,
        string summaryLabel)
    {
        var jobs = Substitute.For<IJobRepository>();
        var clientRegistry = Substitute.For<IClientRegistry>();
        clientRegistry.GetReviewEveryIncrementEnabledAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(false);
        jobs.GetLatestEngagedRevisionAsync(
                ClientId,
                "https://dev.azure.com/org",
                "project",
                "repo-1",
                42,
                Arg.Any<CancellationToken>())
            .Returns((EngagedReviewRevision?)null);
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryAddReviewJobResult(true, null, 0));

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance,
            clientRegistry: clientRegistry);

        var outcome = await sut.SynchronizeAsync(
            CreateRequest(activationSource, summaryLabel) with
            {
                CandidateIterationId = 7,
                ReviewRevision = CreateRevision("revision-a"),
            });

        Assert.Equal(PullRequestSynchronizationReviewDecision.Submitted, outcome.ReviewDecision);
        await jobs.Received(1).TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     A push that lands while the first review is still running is left alone, and the running job survives it.
    ///     Skipping without superseding is the point of the guard: cancelling the running review and then declining to
    ///     replace it would leave the pull request with no review at all.
    /// </summary>
    [Theory]
    [InlineData(PullRequestActivationSource.Crawl, "crawl discovery")]
    [InlineData(PullRequestActivationSource.Webhook, "pull request updated")]
    public async Task SynchronizeAsync_NewRevisionWhileEarlierRevisionIsStillReviewing_SkipsWithoutSuperseding(
        PullRequestActivationSource activationSource,
        string summaryLabel)
    {
        var jobs = Substitute.For<IJobRepository>();
        var clientRegistry = Substitute.For<IClientRegistry>();
        clientRegistry.GetReviewEveryIncrementEnabledAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(false);

        var runningJob = new ReviewJob(Guid.NewGuid(), ClientId, "https://dev.azure.com/org", "project", "repo-1", 42, 7)
        {
            Status = JobStatus.Processing,
        };
        jobs.GetActiveJobsForConfigAsync("https://dev.azure.com/org", "project", Arg.Any<CancellationToken>())
            .Returns([runningJob]);
        jobs.GetLatestEngagedRevisionAsync(
                ClientId,
                "https://dev.azure.com/org",
                "project",
                "repo-1",
                42,
                Arg.Any<CancellationToken>())
            .Returns(CreateEngagement("revision-a", 7));

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance,
            clientRegistry: clientRegistry);

        var outcome = await sut.SynchronizeAsync(
            CreateRequest(activationSource, summaryLabel) with
            {
                CandidateIterationId = 8,
                ReviewRevision = CreateRevision("revision-b"),
            });

        Assert.Equal(PullRequestSynchronizationReviewDecision.SubsequentIncrementSkipped, outcome.ReviewDecision);
        Assert.Equal(PullRequestSynchronizationLifecycleDecision.None, outcome.LifecycleDecision);
        await jobs.DidNotReceive().SetSupersededAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await jobs.DidNotReceive().TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
        await jobs.DidNotReceive().AddAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     A push that lands after the first review finished is also left alone: one automatic review per pull
    ///     request, whatever state the earlier job ended in.
    /// </summary>
    [Theory]
    [InlineData(PullRequestActivationSource.Crawl, "crawl discovery")]
    [InlineData(PullRequestActivationSource.Webhook, "pull request updated")]
    public async Task SynchronizeAsync_NewRevisionAfterEarlierRevisionWasReviewed_SkipsTheIncrement(
        PullRequestActivationSource activationSource,
        string summaryLabel)
    {
        var jobs = Substitute.For<IJobRepository>();
        var clientRegistry = Substitute.For<IClientRegistry>();
        clientRegistry.GetReviewEveryIncrementEnabledAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(false);
        jobs.GetActiveJobsForConfigAsync("https://dev.azure.com/org", "project", Arg.Any<CancellationToken>())
            .Returns([]);
        jobs.GetLatestEngagedRevisionAsync(
                ClientId,
                "https://dev.azure.com/org",
                "project",
                "repo-1",
                42,
                Arg.Any<CancellationToken>())
            .Returns(CreateEngagement("revision-a", 7));

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance,
            clientRegistry: clientRegistry);

        var outcome = await sut.SynchronizeAsync(
            CreateRequest(activationSource, summaryLabel) with
            {
                CandidateIterationId = 8,
                ReviewRevision = CreateRevision("revision-b"),
            });

        Assert.Equal(PullRequestSynchronizationReviewDecision.SubsequentIncrementSkipped, outcome.ReviewDecision);
        Assert.Contains(
            outcome.ActionSummaries,
            summary => summary.Contains(
                "already has a review at revision revision-a",
                StringComparison.OrdinalIgnoreCase));
        await jobs.DidNotReceive().TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     A declined increment is recorded, because nothing else can: the head revision reaches the product only
    ///     through whoever last spoke to the provider, and the surfaces that offer a review have not. Without this
    ///     the pull request sits unreviewed with no way for anyone to find out.
    /// </summary>
    [Fact]
    public async Task SynchronizeAsync_DeclinesAnIncrement_RecordsTheRevisionItDeclined()
    {
        var jobs = Substitute.For<IJobRepository>();
        var clientRegistry = Substitute.For<IClientRegistry>();
        clientRegistry.GetReviewEveryIncrementEnabledAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(false);
        jobs.GetActiveJobsForConfigAsync("https://dev.azure.com/org", "project", Arg.Any<CancellationToken>())
            .Returns([]);
        jobs.GetLatestEngagedRevisionAsync(
                ClientId,
                "https://dev.azure.com/org",
                "project",
                "repo-1",
                42,
                Arg.Any<CancellationToken>())
            .Returns(CreateEngagement("revision-a", 7));

        var pendingReviewWriter = Substitute.For<IReviewPrScanPendingReviewWriter>();

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance,
            clientRegistry: clientRegistry,
            prScanPendingReviewWriter: pendingReviewWriter);

        var outcome = await sut.SynchronizeAsync(
            CreateRequest(PullRequestActivationSource.Webhook, "pull request updated") with
            {
                CandidateIterationId = 8,
                ReviewRevision = CreateRevision("revision-b"),
            });

        Assert.Equal(PullRequestSynchronizationReviewDecision.SubsequentIncrementSkipped, outcome.ReviewDecision);
        await pendingReviewWriter.Received(1).SetPendingReviewRevisionAsync(
            ClientId,
            Arg.Any<string>(),
            Arg.Any<string>(),
            "repo-1",
            42,
            "revision-b",
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Only a decline is recorded. A pull request whose increment is being reviewed is not waiting for anyone,
    ///     and recording it as such would offer an action against work already under way.
    /// </summary>
    [Fact]
    public async Task SynchronizeAsync_ProceedsWithTheIncrement_RecordsNothingAsPending()
    {
        var jobs = Substitute.For<IJobRepository>();
        var clientRegistry = Substitute.For<IClientRegistry>();
        clientRegistry.GetReviewEveryIncrementEnabledAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(true);
        jobs.GetActiveJobsForConfigAsync("https://dev.azure.com/org", "project", Arg.Any<CancellationToken>())
            .Returns([]);
        jobs.GetLatestEngagedRevisionAsync(
                ClientId,
                "https://dev.azure.com/org",
                "project",
                "repo-1",
                42,
                Arg.Any<CancellationToken>())
            .Returns(CreateEngagement("revision-a", 7));
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryAddReviewJobResult(true, null, 0));

        var pendingReviewWriter = Substitute.For<IReviewPrScanPendingReviewWriter>();

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance,
            clientRegistry: clientRegistry,
            prScanPendingReviewWriter: pendingReviewWriter);

        await sut.SynchronizeAsync(
            CreateRequest(PullRequestActivationSource.Webhook, "pull request updated") with
            {
                CandidateIterationId = 8,
                ReviewRevision = CreateRevision("revision-b"),
            });

        await pendingReviewWriter.DidNotReceive().SetPendingReviewRevisionAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     A pull request left unreviewed stays unreviewed whether or not that can be advertised, so a failed
    ///     record does not turn a decline into a review.
    /// </summary>
    [Fact]
    public async Task SynchronizeAsync_PendingRecordFails_StillDeclinesTheIncrement()
    {
        var jobs = Substitute.For<IJobRepository>();
        var clientRegistry = Substitute.For<IClientRegistry>();
        clientRegistry.GetReviewEveryIncrementEnabledAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(false);
        jobs.GetActiveJobsForConfigAsync("https://dev.azure.com/org", "project", Arg.Any<CancellationToken>())
            .Returns([]);
        jobs.GetLatestEngagedRevisionAsync(
                ClientId,
                "https://dev.azure.com/org",
                "project",
                "repo-1",
                42,
                Arg.Any<CancellationToken>())
            .Returns(CreateEngagement("revision-a", 7));

        var pendingReviewWriter = Substitute.For<IReviewPrScanPendingReviewWriter>();
        pendingReviewWriter.SetPendingReviewRevisionAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("scan store unavailable"));

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance,
            clientRegistry: clientRegistry,
            prScanPendingReviewWriter: pendingReviewWriter);

        var outcome = await sut.SynchronizeAsync(
            CreateRequest(PullRequestActivationSource.Webhook, "pull request updated") with
            {
                CandidateIterationId = 8,
                ReviewRevision = CreateRevision("revision-b"),
            });

        Assert.Equal(PullRequestSynchronizationReviewDecision.SubsequentIncrementSkipped, outcome.ReviewDecision);
        await jobs.DidNotReceive().TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Replying to a reviewer thread on the revision that was reviewed is a request, not an increment: the head
    ///     still matches the revision this client engaged with, so the guard stays out of the way.
    /// </summary>
    [Fact]
    public async Task SynchronizeAsync_ReplyOnTheReviewedRevision_StillQueuesAReview()
    {
        var jobs = Substitute.For<IJobRepository>();
        var threadStatusFetcher = Substitute.For<IReviewerThreadStatusFetcher>();
        var threadMemoryService = Substitute.For<IThreadMemoryService>();
        var scanRepository = Substitute.For<IReviewPrScanRepository>();
        var clientRegistry = Substitute.For<IClientRegistry>();
        clientRegistry.GetReviewEveryIncrementEnabledAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(false);

        jobs.GetActiveJobsForConfigAsync("https://dev.azure.com/org", "project", Arg.Any<CancellationToken>())
            .Returns([]);
        jobs.GetLatestEngagedRevisionAsync(
                ClientId,
                "https://dev.azure.com/org",
                "project",
                "repo-1",
                42,
                Arg.Any<CancellationToken>())
            .Returns(CreateEngagement("revision-a", 7));
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryAddReviewJobResult(true, null, 0));

        var scan = new ReviewPrScan(Guid.NewGuid(), ClientId, "https://provider.example", "project", "repo-1", 42, "revision-a");
        scan.Threads.Add(
            new ReviewPrScanThread
            {
                ReviewPrScanId = scan.Id,
                ThreadId = "17",
                LastSeenReplyCount = 0,
                LastSeenStatus = "Active",
            });
        scanRepository.GetAsync(ClientId, Arg.Any<string>(), Arg.Any<string>(), "repo-1", 42, Arg.Any<CancellationToken>())
            .Returns(scan);
        threadStatusFetcher.GetReviewerThreadStatusesAsync(
                "https://dev.azure.com/org",
                "project",
                "repo-1",
                42,
                Arg.Any<ThreadOwnershipResolver>(),
                ClientId,
                Arg.Any<CancellationToken>())
            .Returns(
            [
                new PrThreadStatusEntry("17", "Active", "/src/file.ts", "Bot: comment\nUser: new reply", 1),
            ]);

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance,
            null,
            threadStatusFetcher,
            threadMemoryService,
            scanRepository,
            clientRegistry);

        var outcome = await sut.SynchronizeAsync(
            CreateRequest(PullRequestActivationSource.Crawl, "crawl discovery") with
            {
                CandidateIterationId = 7,
                ReviewRevision = CreateRevision("revision-a"),
                RequestedReviewerIdentity = CreateRequestedReviewerIdentity(),
            });

        Assert.Equal(PullRequestSynchronizationReviewDecision.Submitted, outcome.ReviewDecision);
        await jobs.Received(1).TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Duplicate detection stays ahead of the guard. A default client whose pull request has not moved on
    ///     still reaches the reservation check, so a second review of the revision already being reviewed is
    ///     refused as the duplicate it is rather than passed off as an increment nobody asked to review.
    /// </summary>
    [Fact]
    public async Task SynchronizeAsync_WhenTheEngagedRevisionIsStillBeingReviewed_ReturnsDuplicateOutcome()
    {
        var jobs = Substitute.For<IJobRepository>();
        var clientRegistry = Substitute.For<IClientRegistry>();
        clientRegistry.GetReviewEveryIncrementEnabledAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(false);

        var runningJob = new ReviewJob(
            Guid.NewGuid(),
            ClientId,
            "https://dev.azure.com/org",
            "project",
            "repo-1",
            42,
            7)
        {
            Status = JobStatus.Processing,
        };
        runningJob.SetReviewRevision(CreateRevision("revision-a"));
        jobs.GetActiveJobsForConfigAsync("https://dev.azure.com/org", "project", Arg.Any<CancellationToken>())
            .Returns([runningJob]);
        jobs.GetLatestEngagedRevisionAsync(
                ClientId,
                "https://dev.azure.com/org",
                "project",
                "repo-1",
                42,
                Arg.Any<CancellationToken>())
            .Returns(CreateEngagement("revision-a", 7));
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryAddReviewJobResult(true, null, 0));

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance,
            clientRegistry: clientRegistry);

        var outcome = await sut.SynchronizeAsync(
            CreateRequest(PullRequestActivationSource.Crawl, "crawl discovery") with
            {
                CandidateIterationId = 7,
                ReviewRevision = CreateRevision("revision-a"),
            });

        Assert.Equal(PullRequestSynchronizationReviewDecision.DuplicateActiveJob, outcome.ReviewDecision);
        Assert.Equal(runningJob.Id, outcome.JobId);
        await jobs.DidNotReceive().SetSupersededAsync(runningJob.Id, Arg.Any<CancellationToken>());
        await jobs.DidNotReceive().TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     A job held at the budget cap never produced a review, so it is no engagement with its revision. With
    ///     nothing else on record the next push is reviewed rather than declined forever.
    /// </summary>
    [Fact]
    public async Task SynchronizeAsync_WhenTheOnlyJobWasHeldAtTheBudgetCap_ReviewsTheNextPush()
    {
        var jobs = Substitute.For<IJobRepository>();
        var scanRepository = Substitute.For<IReviewPrScanRepository>();
        var clientRegistry = Substitute.For<IClientRegistry>();
        clientRegistry.GetReviewEveryIncrementEnabledAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(false);

        // The engagement query excludes budget-blocked jobs, and a job that never ran wrote no scan watermark.
        jobs.GetLatestEngagedRevisionAsync(
                ClientId,
                "https://dev.azure.com/org",
                "project",
                "repo-1",
                42,
                Arg.Any<CancellationToken>())
            .Returns((EngagedReviewRevision?)null);
        jobs.GetActiveJobsForConfigAsync("https://dev.azure.com/org", "project", Arg.Any<CancellationToken>())
            .Returns([]);
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryAddReviewJobResult(true, null, 0));
        scanRepository.GetAsync(ClientId, Arg.Any<string>(), Arg.Any<string>(), "repo-1", 42, Arg.Any<CancellationToken>())
            .Returns((ReviewPrScan?)null);

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance,
            prScanRepository: scanRepository,
            clientRegistry: clientRegistry);

        var outcome = await sut.SynchronizeAsync(
            CreateRequest(PullRequestActivationSource.Crawl, "crawl discovery") with
            {
                CandidateIterationId = 8,
                ReviewRevision = CreateRevision("revision-b"),
            });

        Assert.Equal(PullRequestSynchronizationReviewDecision.Submitted, outcome.ReviewDecision);
        await jobs.Received(1)
            .TryAddIfNoActiveDuplicateAsync(
                Arg.Is<ReviewJob>(job => job.ProviderRevisionId == "revision-b"),
                Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     A review that finds nothing deletes its own job row after writing the scan watermark. The watermark is
    ///     therefore the durable record of engagement, and a later push is still declined without it.
    /// </summary>
    [Fact]
    public async Task SynchronizeAsync_WithoutAJobRowButAScanWatermarkAtAnotherRevision_SkipsTheIncrement()
    {
        var jobs = Substitute.For<IJobRepository>();
        var scanRepository = Substitute.For<IReviewPrScanRepository>();
        var clientRegistry = Substitute.For<IClientRegistry>();
        clientRegistry.GetReviewEveryIncrementEnabledAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(false);

        jobs.GetLatestEngagedRevisionAsync(
                ClientId,
                "https://dev.azure.com/org",
                "project",
                "repo-1",
                42,
                Arg.Any<CancellationToken>())
            .Returns((EngagedReviewRevision?)null);
        scanRepository.GetAsync(ClientId, Arg.Any<string>(), Arg.Any<string>(), "repo-1", 42, Arg.Any<CancellationToken>())
            .Returns(new ReviewPrScan(Guid.NewGuid(), ClientId, "https://provider.example", "project", "repo-1", 42, "revision-a"));

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance,
            prScanRepository: scanRepository,
            clientRegistry: clientRegistry);

        var outcome = await sut.SynchronizeAsync(
            CreateRequest(PullRequestActivationSource.Crawl, "crawl discovery") with
            {
                CandidateIterationId = 8,
                ReviewRevision = CreateRevision("revision-b"),
            });

        Assert.Equal(PullRequestSynchronizationReviewDecision.SubsequentIncrementSkipped, outcome.ReviewDecision);
        Assert.Contains(
            outcome.ActionSummaries,
            summary => summary.Contains(
                "already has a review at revision revision-a",
                StringComparison.OrdinalIgnoreCase));
        await jobs.DidNotReceive().TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     A client that opted in keeps review-every-push: the job at the older revision is superseded and the new
    ///     revision is reviewed.
    /// </summary>
    [Theory]
    [InlineData(PullRequestActivationSource.Crawl, "crawl discovery")]
    [InlineData(PullRequestActivationSource.Webhook, "pull request updated")]
    public async Task SynchronizeAsync_WhenClientReviewsEveryIncrement_SupersedesOlderJobAndReviewsNewRevision(
        PullRequestActivationSource activationSource,
        string summaryLabel)
    {
        var jobs = Substitute.For<IJobRepository>();
        var clientRegistry = Substitute.For<IClientRegistry>();
        clientRegistry.GetReviewEveryIncrementEnabledAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(true);

        var runningJob = new ReviewJob(Guid.NewGuid(), ClientId, "https://dev.azure.com/org", "project", "repo-1", 42, 7)
        {
            Status = JobStatus.Processing,
        };
        jobs.GetActiveJobsForConfigAsync("https://dev.azure.com/org", "project", Arg.Any<CancellationToken>())
            .Returns([runningJob]);
        jobs.GetLatestEngagedRevisionAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateEngagement("revision-a", 7));
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryAddReviewJobResult(true, null, 0));

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance,
            clientRegistry: clientRegistry);

        var outcome = await sut.SynchronizeAsync(
            CreateRequest(activationSource, summaryLabel) with
            {
                CandidateIterationId = 8,
                ReviewRevision = CreateRevision("revision-b"),
            });

        Assert.Equal(PullRequestSynchronizationReviewDecision.Submitted, outcome.ReviewDecision);
        Assert.Equal(PullRequestSynchronizationLifecycleDecision.CancelledActiveJobs, outcome.LifecycleDecision);
        await jobs.Received(1).SetSupersededAsync(runningJob.Id, Arg.Any<CancellationToken>());
        await jobs.Received(1)
            .TryAddIfNoActiveDuplicateAsync(
                Arg.Is<ReviewJob>(job => job.IterationId == 8),
                Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Only automatic triggers are guarded. Driving every activation source pins which ones those are, so a
    ///     source added later fails here and forces a deliberate decision about whether it belongs inside the guard.
    ///     A requested review is deliberately outside it: asking for one is the answer to a declined increment.
    /// </summary>
    [Fact]
    public async Task SynchronizeAsync_GuardsExactlyTheAutomaticActivationSources()
    {
        var observed = new List<string>();

        foreach (var activationSource in Enum.GetValues<PullRequestActivationSource>())
        {
            var jobs = Substitute.For<IJobRepository>();
            var clientRegistry = Substitute.For<IClientRegistry>();
            clientRegistry.GetReviewEveryIncrementEnabledAsync(ClientId, Arg.Any<CancellationToken>())
                .Returns(false);
            jobs.GetActiveJobsForConfigAsync("https://dev.azure.com/org", "project", Arg.Any<CancellationToken>())
                .Returns([]);
            jobs.GetLatestEngagedRevisionAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<int>(),
                    Arg.Any<CancellationToken>())
                .Returns(CreateEngagement("revision-a", 7));
            jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
                .Returns(new TryAddReviewJobResult(true, null, 0));

            var sut = new PullRequestSynchronizationService(
                jobs,
                NullLogger<PullRequestSynchronizationService>.Instance,
                clientRegistry: clientRegistry);

            var outcome = await sut.SynchronizeAsync(
                CreateRequest(activationSource, "activation") with
                {
                    CandidateIterationId = 8,
                    ReviewRevision = CreateRevision("revision-b"),
                });

            var guarded = outcome.ReviewDecision
                          == PullRequestSynchronizationReviewDecision.SubsequentIncrementSkipped;
            observed.Add($"{activationSource}:{(guarded ? "guarded" : "unguarded")}");
        }

        Assert.Equal(["Crawl:guarded", "Webhook:guarded", "Manual:unguarded"], observed);
    }

    /// <summary>
    ///     A caller that opted out of the change-detection heuristics passes the guard too, whatever activation
    ///     source carries the request. The guard is one of those heuristics, so honouring the flag directly keeps it
    ///     from becoming the one exception a requested review cannot get past.
    /// </summary>
    [Fact]
    public async Task SynchronizeAsync_WhenUnchangedResubmissionIsAllowed_IsNotGuarded()
    {
        var jobs = Substitute.For<IJobRepository>();
        var clientRegistry = Substitute.For<IClientRegistry>();
        clientRegistry.GetReviewEveryIncrementEnabledAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(false);
        jobs.GetActiveJobsForConfigAsync("https://dev.azure.com/org", "project", Arg.Any<CancellationToken>())
            .Returns([]);
        jobs.GetLatestEngagedRevisionAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateEngagement("revision-a", 7));
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryAddReviewJobResult(true, null, 0));

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance,
            clientRegistry: clientRegistry);

        var outcome = await sut.SynchronizeAsync(
            CreateRequest(PullRequestActivationSource.Crawl, "crawl discovery") with
            {
                CandidateIterationId = 8,
                ReviewRevision = CreateRevision("revision-b"),
                AllowUnchangedResubmission = true,
            });

        Assert.NotEqual(
            PullRequestSynchronizationReviewDecision.SubsequentIncrementSkipped,
            outcome.ReviewDecision);
        await jobs.Received(1)
            .TryAddIfNoActiveDuplicateAsync(
                Arg.Is<ReviewJob>(job => job.IterationId == 8),
                Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     A provider that resolves no revision leaves the iteration id as the only identity the pass has. The guard
    ///     keys on that fallback rather than opting out.
    /// </summary>
    [Fact]
    public async Task SynchronizeAsync_WithoutAResolvableRevision_GuardsOnTheIterationIdFallback()
    {
        var jobs = Substitute.For<IJobRepository>();
        var clientRegistry = Substitute.For<IClientRegistry>();
        clientRegistry.GetReviewEveryIncrementEnabledAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(false);
        jobs.GetLatestEngagedRevisionAsync(
                ClientId,
                "https://dev.azure.com/org",
                "project",
                "repo-1",
                42,
                Arg.Any<CancellationToken>())
            .Returns(new EngagedReviewRevision("7", null, 7));

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance,
            clientRegistry: clientRegistry);

        var outcome = await sut.SynchronizeAsync(
            CreateRequest(PullRequestActivationSource.Crawl, "crawl discovery") with
            {
                CandidateIterationId = 8,
                ReviewRevision = null,
            });

        Assert.Equal(PullRequestSynchronizationReviewDecision.SubsequentIncrementSkipped, outcome.ReviewDecision);
        await jobs.Received(1)
            .GetLatestEngagedRevisionAsync(
                ClientId,
                "https://dev.azure.com/org",
                "project",
                "repo-1",
                42,
                Arg.Any<CancellationToken>());
        await jobs.DidNotReceive().TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Minimal wirings construct the service without a client registry, so the per-client setting cannot be read.
    ///     The guard then does not apply and synchronization behaves as it always did.
    /// </summary>
    [Fact]
    public async Task SynchronizeAsync_WithoutAClientRegistry_IsNotGuarded()
    {
        var jobs = Substitute.For<IJobRepository>();
        jobs.GetActiveJobsForConfigAsync("https://dev.azure.com/org", "project", Arg.Any<CancellationToken>())
            .Returns([]);
        jobs.GetLatestEngagedRevisionAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateEngagement("revision-a", 7));
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryAddReviewJobResult(true, null, 0));

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance);

        var outcome = await sut.SynchronizeAsync(
            CreateRequest(PullRequestActivationSource.Webhook, "pull request updated") with
            {
                CandidateIterationId = 8,
                ReviewRevision = CreateRevision("revision-b"),
            });

        Assert.Equal(PullRequestSynchronizationReviewDecision.Submitted, outcome.ReviewDecision);
        await jobs.DidNotReceive()
            .GetLatestEngagedRevisionAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>());
    }

    private static EngagedReviewRevision CreateEngagement(string providerRevisionId, int iterationId)
    {
        return new EngagedReviewRevision(providerRevisionId, CreateRevision(providerRevisionId), iterationId);
    }

    private static ReviewRevision CreateRevision(string providerRevisionId)
    {
        return new ReviewRevision("head-sha", "base-sha", "start-sha", providerRevisionId, "patch-identity");
    }

    [Fact]
    public async Task SynchronizeAsync_WhenResubmissionIsAllowed_ReviewsARevisionThatHasNotChanged()
    {
        // The change-detection heuristics exist to stop the automatic loop reviewing the same revision
        // forever. An explicitly requested review is the action they defer to, so it passes through them.
        var jobs = Substitute.For<IJobRepository>();
        var threadStatusFetcher = Substitute.For<IReviewerThreadStatusFetcher>();
        var scanRepository = Substitute.For<IReviewPrScanRepository>();

        jobs.FindActiveJob("https://dev.azure.com/org", "project", "repo-1", 42, 7).Returns((ReviewJob?)null);
        jobs.FindCompletedJob("https://dev.azure.com/org", "project", "repo-1", 42, 7)
            .Returns(new ReviewJob(Guid.NewGuid(), ClientId, "https://dev.azure.com/org", "project", "repo-1", 42, 7));
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryAddReviewJobResult(true, null, 0));

        var scan = new ReviewPrScan(Guid.NewGuid(), ClientId, "https://provider.example", "project", "repo-1", 42, "7");
        scanRepository.GetAsync(ClientId, Arg.Any<string>(), Arg.Any<string>(), "repo-1", 42, Arg.Any<CancellationToken>()).Returns(scan);

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance,
            Substitute.For<IPullRequestIterationResolver>(),
            threadStatusFetcher,
            Substitute.For<IThreadMemoryService>(),
            scanRepository);

        var outcome = await sut.SynchronizeAsync(
            CreateRequest(PullRequestActivationSource.Manual, "an explicit review request") with
            {
                CandidateIterationId = 7,
                AllowUnchangedResubmission = true,
            });

        Assert.Equal(PullRequestSynchronizationReviewDecision.Submitted, outcome.ReviewDecision);
        await jobs.Received(1)
            .TryAddIfNoActiveDuplicateAsync(Arg.Is<ReviewJob>(job => job.IterationId == 7), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SynchronizeAsync_WhenResubmissionIsAllowed_ReviewsARevisionAPriorReviewFailedAt()
    {
        // Suppressing automatic re-review after a failure is exactly what a manual restart overrides.
        var jobs = Substitute.For<IJobRepository>();

        jobs.FindActiveJob("https://dev.azure.com/org", "project", "repo-1", 42, 7).Returns((ReviewJob?)null);
        jobs.FindCompletedJob("https://dev.azure.com/org", "project", "repo-1", 42, 7).Returns((ReviewJob?)null);
        jobs.FindFailedJob("https://dev.azure.com/org", "project", "repo-1", 42, 7)
            .Returns(
                new ReviewJob(Guid.NewGuid(), ClientId, "https://dev.azure.com/org", "project", "repo-1", 42, 7)
                {
                    Status = JobStatus.Failed,
                });
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryAddReviewJobResult(true, null, 0));

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance);

        var outcome = await sut.SynchronizeAsync(
            CreateRequest(PullRequestActivationSource.Manual, "an explicit review request") with
            {
                CandidateIterationId = 7,
                AllowUnchangedResubmission = true,
            });

        Assert.Equal(PullRequestSynchronizationReviewDecision.Submitted, outcome.ReviewDecision);
    }

    [Fact]
    public async Task SynchronizeAsync_WhenResubmissionIsAllowed_StillSkipsAnActiveDuplicate()
    {
        // Bypassing the change-detection heuristics must not bypass duplicate detection: two jobs for the
        // same revision would review it twice and pay twice.
        var jobs = Substitute.For<IJobRepository>();
        var existing = new ReviewJob(Guid.NewGuid(), ClientId, "https://dev.azure.com/org", "project", "repo-1", 42, 7)
        {
            Status = JobStatus.Pending,
        };
        jobs.FindActiveJob("https://dev.azure.com/org", "project", "repo-1", 42, 7).Returns(existing);

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance);

        var outcome = await sut.SynchronizeAsync(
            CreateRequest(PullRequestActivationSource.Manual, "an explicit review request") with
            {
                CandidateIterationId = 7,
                AllowUnchangedResubmission = true,
            });

        Assert.Equal(PullRequestSynchronizationReviewDecision.DuplicateActiveJob, outcome.ReviewDecision);
        Assert.Equal(existing.Id, outcome.JobId);
        await jobs.DidNotReceive().TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SynchronizeAsync_WhenAJobIsQueued_ReportsItsIdentifier()
    {
        // A caller that triggered the review has nothing to poll unless the queued job's id comes back.
        var jobs = Substitute.For<IJobRepository>();
        jobs.FindActiveJob("https://dev.azure.com/org", "project", "repo-1", 42, 7).Returns((ReviewJob?)null);
        jobs.FindCompletedJob("https://dev.azure.com/org", "project", "repo-1", 42, 7).Returns((ReviewJob?)null);
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryAddReviewJobResult(true, null, 0));

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance);

        var outcome = await sut.SynchronizeAsync(CreateRequest(PullRequestActivationSource.Crawl, "crawl discovery") with { CandidateIterationId = 7 });

        Assert.Equal(PullRequestSynchronizationReviewDecision.Submitted, outcome.ReviewDecision);
        Assert.NotNull(outcome.JobId);
        await jobs.Received(1)
            .TryAddIfNoActiveDuplicateAsync(
                Arg.Is<ReviewJob>(job => job.Id == outcome.JobId),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SynchronizeAsync_WhenTheReservationFindsADuplicate_ReportsThatJobsIdentifier()
    {
        var jobs = Substitute.For<IJobRepository>();
        var duplicateJob = new ReviewJob(Guid.NewGuid(), ClientId, "https://dev.azure.com/org", "project", "repo-1", 42, 7)
        {
            Status = JobStatus.Pending,
        };

        jobs.FindActiveJob("https://dev.azure.com/org", "project", "repo-1", 42, 7).Returns((ReviewJob?)null);
        jobs.FindCompletedJob("https://dev.azure.com/org", "project", "repo-1", 42, 7).Returns((ReviewJob?)null);
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryAddReviewJobResult(false, duplicateJob, 0));

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance);

        var outcome = await sut.SynchronizeAsync(
            CreateRequest(PullRequestActivationSource.Webhook, "pull request updated") with
            {
                CandidateIterationId = 7,
            });

        Assert.Equal(PullRequestSynchronizationReviewDecision.DuplicateActiveJob, outcome.ReviewDecision);
        Assert.Equal(duplicateJob.Id, outcome.JobId);
    }

    [Fact]
    public async Task SynchronizeAsync_WhenAnActiveJobAlreadyHoldsTheRevision_ReportsThatJobsIdentifier()
    {
        var jobs = Substitute.For<IJobRepository>();
        var revision = new ReviewRevision("head-sha", "base-sha", "start-sha", "7", "patch-1");
        var existing = new ReviewJob(Guid.NewGuid(), ClientId, "https://dev.azure.com/org", "project", "repo-1", 42, 7)
        {
            Status = JobStatus.Pending,
        };
        existing.SetReviewRevision(revision);

        jobs.GetActiveJobsForConfigAsync("https://dev.azure.com/org", "project", Arg.Any<CancellationToken>())
            .Returns([existing]);

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance);

        var outcome = await sut.SynchronizeAsync(
            CreateRequest(PullRequestActivationSource.Manual, "an explicit review request") with
            {
                CandidateIterationId = 7,
                ReviewRevision = revision,
                AllowUnchangedResubmission = true,
            });

        Assert.Equal(PullRequestSynchronizationReviewDecision.DuplicateActiveJob, outcome.ReviewDecision);
        Assert.Equal(existing.Id, outcome.JobId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SynchronizeAsync_StampsTheQueuedJobWithWhetherTheReviewWasAskedForExplicitly(bool allowUnchangedResubmission)
    {
        // The same rule runs again when the job executes, where it deletes the job rather than recording a
        // skip. Execution can only honour an explicit request if the job itself carries it.
        var jobs = Substitute.For<IJobRepository>();
        jobs.FindActiveJob("https://dev.azure.com/org", "project", "repo-1", 42, 7).Returns((ReviewJob?)null);
        jobs.FindCompletedJob("https://dev.azure.com/org", "project", "repo-1", 42, 7).Returns((ReviewJob?)null);
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryAddReviewJobResult(true, null, 0));

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance);

        var outcome = await sut.SynchronizeAsync(
            CreateRequest(PullRequestActivationSource.Manual, "an explicit review request") with
            {
                CandidateIterationId = 7,
                AllowUnchangedResubmission = allowUnchangedResubmission,
            });

        Assert.Equal(PullRequestSynchronizationReviewDecision.Submitted, outcome.ReviewDecision);
        await jobs.Received(1)
            .TryAddIfNoActiveDuplicateAsync(
                Arg.Is<ReviewJob>(job => job.AllowUnchangedResubmission == allowUnchangedResubmission),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SynchronizeAsync_WhenAJobIsQueued_StoresTheRevisionTheRequestSupplied()
    {
        // The commit identity is the whole point of the job: it is what the review runs against, what tells
        // this revision from the next one, and what a caller supplying coordinates alone came here to have
        // resolved. A job queued without it would review something nobody named.
        var jobs = Substitute.For<IJobRepository>();
        var revision = new ReviewRevision("head-sha", "base-sha", "start-sha", "7", "base-sha...head-sha");

        jobs.FindActiveJob("https://dev.azure.com/org", "project", "repo-1", 42, 7).Returns((ReviewJob?)null);
        jobs.FindCompletedJob("https://dev.azure.com/org", "project", "repo-1", 42, 7).Returns((ReviewJob?)null);
        jobs.GetActiveJobsForConfigAsync("https://dev.azure.com/org", "project", Arg.Any<CancellationToken>())
            .Returns([]);
        jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryAddReviewJobResult(true, null, 0));

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance);

        var outcome = await sut.SynchronizeAsync(
            CreateRequest(PullRequestActivationSource.Manual, "an explicit review request") with
            {
                CandidateIterationId = 7,
                ReviewRevision = revision,
                AllowUnchangedResubmission = true,
            });

        Assert.Equal(PullRequestSynchronizationReviewDecision.Submitted, outcome.ReviewDecision);
        await jobs.Received(1)
            .TryAddIfNoActiveDuplicateAsync(
                Arg.Is<ReviewJob>(job =>
                    job.ReviewRevisionReference != null
                    && job.ReviewRevisionReference.HeadSha == "head-sha"
                    && job.ReviewRevisionReference.BaseSha == "base-sha"
                    && job.ReviewRevisionReference.StartSha == "start-sha"
                    && job.ReviewRevisionReference.ProviderRevisionId == "7"),
                Arg.Any<CancellationToken>());
    }

    private static PullRequestSynchronizationRequest CreateRequest(
        PullRequestActivationSource activationSource,
        string summaryLabel)
    {
        return new PullRequestSynchronizationRequest
        {
            ActivationSource = activationSource,
            SummaryLabel = summaryLabel,
            ClientId = ClientId,
            ProviderScopePath = "https://dev.azure.com/org",
            ProviderProjectKey = "project",
            RepositoryId = "repo-1",
            PullRequestId = 42,
            PullRequestStatus = PrStatus.Active,
        };
    }

    private static ReviewerIdentity CreateRequestedReviewerIdentity()
    {
        var host = new ProviderHostRef(ScmProvider.AzureDevOps, "https://dev.azure.com/org");
        return new ReviewerIdentity(host, ReviewerId.ToString("D"), "review-bot", "Review Bot", true);
    }
}

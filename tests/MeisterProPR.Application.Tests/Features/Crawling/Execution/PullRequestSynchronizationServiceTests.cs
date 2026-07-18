// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Crawling.Execution.Models;
using MeisterProPR.Application.Features.Crawling.Execution.Services;
using MeisterProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.Events;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Application.Tests.Features.Crawling.Execution;

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

        var scan = new ReviewPrScan(Guid.NewGuid(), ClientId, "repo-1", 42, "7");
        scan.Threads.Add(
            new ReviewPrScanThread
            {
                ReviewPrScanId = scan.Id,
                ThreadId = 17,
                LastSeenReplyCount = 0,
                LastSeenStatus = "Active",
            });

        scanRepository.GetAsync(ClientId, "repo-1", 42, Arg.Any<CancellationToken>())
            .Returns(scan);
        threadStatusFetcher.GetReviewerThreadStatusesAsync(
                "https://dev.azure.com/org",
                "project",
                "repo-1",
                42,
                ReviewerId,
                ClientId,
                Arg.Any<CancellationToken>())
            .Returns(
            [
                new PrThreadStatusEntry(17, "Fixed", "/src/file.ts", "Bot: comment\nUser: reply", 1),
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
                    evt.ThreadId == 17),
                Arg.Any<CancellationToken>());
        await scanRepository.Received(1)
            .UpsertAsync(
                Arg.Is<ReviewPrScan>(updated =>
                    updated.Threads.Any(thread => thread.ThreadId == 17 && thread.LastSeenStatus == "Fixed")),
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

        var scan = new ReviewPrScan(Guid.NewGuid(), ClientId, "repo-1", 42, "7");
        scan.Threads.Add(
            new ReviewPrScanThread
            {
                ReviewPrScanId = scan.Id,
                ThreadId = 17,
                LastSeenReplyCount = 1,
                LastSeenStatus = "Active",
            });

        scanRepository.GetAsync(ClientId, "repo-1", 42, Arg.Any<CancellationToken>())
            .Returns(scan);
        threadStatusFetcher.GetReviewerThreadStatusesAsync(
                "https://dev.azure.com/org",
                "project",
                "repo-1",
                42,
                ReviewerId,
                ClientId,
                Arg.Any<CancellationToken>())
            .Returns(
            [
                new PrThreadStatusEntry(17, "Active", "/src/file.ts", "Bot: comment\nUser: reply", 1),
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
        var scan = new ReviewPrScan(Guid.NewGuid(), ClientId, "repo-1", 42, "7");
        scan.Threads.Add(
            new ReviewPrScanThread
            {
                ReviewPrScanId = scan.Id,
                ThreadId = 17,
                LastSeenReplyCount = 0,
                LastSeenStatus = "Active",
            });
        scanRepository.GetAsync(ClientId, "repo-1", 42, Arg.Any<CancellationToken>())
            .Returns(scan);
        threadStatusFetcher.GetReviewerThreadStatusesAsync(
                "https://dev.azure.com/org",
                "project",
                "repo-1",
                42,
                ReviewerId,
                ClientId,
                Arg.Any<CancellationToken>())
            .Returns(
            [
                new PrThreadStatusEntry(17, "Active", "/src/file.ts", "Bot: comment\nUser: new reply", 1),
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

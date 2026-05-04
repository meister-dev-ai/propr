// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Crawling.Execution.Models;
using MeisterProPR.Application.Features.Crawling.Execution.Services;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.Events;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Application.Tests.Features.Crawling.Execution;

public sealed class PullRequestSynchronizationServiceProviderTests
{
    private static readonly Guid ClientId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ReviewerId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task SynchronizeAsync_WithNormalizedGitHubContext_QueuesReviewJobWithProviderNeutralMetadata()
    {
        var jobs = Substitute.For<IJobRepository>();
        jobs.GetActiveJobsForConfigAsync("https://dev.azure.com/org", "project", Arg.Any<CancellationToken>())
            .Returns([]);
        jobs.FindActiveJob("https://dev.azure.com/org", "project", "repo-gh-1", 42, 11)
            .Returns((ReviewJob?)null);
        jobs.FindCompletedJob("https://dev.azure.com/org", "project", "repo-gh-1", 42, 11)
            .Returns((ReviewJob?)null);

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance);

        var outcome = await sut.SynchronizeAsync(
            CreateGitHubRequest(PullRequestActivationSource.Webhook, "pull request updated") with
            {
                CandidateIterationId = 11,
            });

        Assert.Equal(PullRequestSynchronizationReviewDecision.Submitted, outcome.ReviewDecision);
        Assert.Equal(PullRequestSynchronizationLifecycleDecision.None, outcome.LifecycleDecision);
        Assert.Contains(
            outcome.ActionSummaries,
            summary => summary.Contains("Submitted review intake job", StringComparison.OrdinalIgnoreCase));

        await jobs.Received(1)
            .AddAsync(
                Arg.Is<ReviewJob>(job =>
                    job.Provider == ScmProvider.GitHub &&
                    job.HostBaseUrl == "https://github.com" &&
                    job.RepositoryId == "repo-gh-1" &&
                    job.RepositoryOwnerOrNamespace == "acme" &&
                    job.RepositoryProjectPath == "acme/propr" &&
                    job.ExternalCodeReviewId == "42" &&
                    job.CodeReviewPlatformKind == CodeReviewPlatformKind.PullRequest &&
                    job.RevisionHeadSha == "head-sha" &&
                    job.RevisionBaseSha == "base-sha" &&
                    job.RevisionStartSha == "start-sha" &&
                    job.ProviderRevisionId == "revision-1" &&
                    job.ReviewPatchIdentity == "patch-1"),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SynchronizeAsync_WithNormalizedGitHubContext_PreservesThreadMemoryTransitions()
    {
        var jobs = Substitute.For<IJobRepository>();
        var threadStatusFetcher = Substitute.For<IReviewerThreadStatusFetcher>();
        var threadMemoryService = Substitute.For<IThreadMemoryService>();
        var scanRepository = Substitute.For<IReviewPrScanRepository>();

        jobs.GetActiveJobsForConfigAsync("https://dev.azure.com/org", "project", Arg.Any<CancellationToken>())
            .Returns([]);

        jobs.FindActiveJob("https://dev.azure.com/org", "project", "repo-gh-1", 42, 11)
            .Returns((ReviewJob?)null);
        jobs.FindCompletedJob("https://dev.azure.com/org", "project", "repo-gh-1", 42, 11)
            .Returns((ReviewJob?)null);

        var scan = new ReviewPrScan(Guid.NewGuid(), ClientId, "repo-gh-1", 42, "11");
        scan.Threads.Add(
            new ReviewPrScanThread
            {
                ReviewPrScanId = scan.Id,
                ThreadId = 17,
                LastSeenReplyCount = 0,
                LastSeenStatus = "Active",
            });

        scanRepository.GetAsync(ClientId, "repo-gh-1", 42, Arg.Any<CancellationToken>())
            .Returns(scan);
        threadStatusFetcher.GetReviewerThreadStatusesAsync(
                "https://dev.azure.com/org",
                "project",
                "repo-gh-1",
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
            threadStatusFetcher: threadStatusFetcher,
            threadMemoryService: threadMemoryService,
            prScanRepository: scanRepository);

        var outcome = await sut.SynchronizeAsync(
            CreateGitHubRequest(PullRequestActivationSource.Webhook, "pull request updated") with
            {
                CandidateIterationId = 11,
                RequestedReviewerIdentity =
                new ReviewerIdentity(
                    new ProviderHostRef(ScmProvider.GitHub, "https://github.com"),
                    ReviewerId.ToString("D"),
                    "meister-dev-bot",
                    "Meister Dev Bot",
                    true),
            });

        Assert.Equal(PullRequestSynchronizationReviewDecision.Submitted, outcome.ReviewDecision);

        await threadMemoryService.Received(1)
            .HandleThreadResolvedAsync(
                Arg.Is<ThreadResolvedDomainEvent>(evt =>
                    evt.ClientId == ClientId &&
                    evt.RepositoryId == "repo-gh-1" &&
                    evt.PullRequestId == 42 &&
                    evt.ThreadId == 17),
                Arg.Any<CancellationToken>());
        await scanRepository.Received(1)
            .UpsertAsync(
                Arg.Is<ReviewPrScan>(updated =>
                    updated.Threads.Any(thread => thread.ThreadId == 17 && thread.LastSeenStatus == "Fixed")),
                Arg.Any<CancellationToken>());
        await jobs.Received(1)
            .AddAsync(
                Arg.Is<ReviewJob>(job =>
                    job.Provider == ScmProvider.GitHub &&
                    job.RevisionHeadSha == "head-sha" &&
                    job.RevisionBaseSha == "base-sha"),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SynchronizeAsync_WithMatchingProviderRevisionAndNoReplies_SkipsReviewWork()
    {
        var jobs = Substitute.For<IJobRepository>();
        var threadStatusFetcher = Substitute.For<IReviewerThreadStatusFetcher>();
        var scanRepository = Substitute.For<IReviewPrScanRepository>();

        jobs.GetActiveJobsForConfigAsync("https://dev.azure.com/org", "project", Arg.Any<CancellationToken>())
            .Returns([]);

        jobs.FindActiveJob("https://dev.azure.com/org", "project", "repo-gh-1", 42, 11)
            .Returns((ReviewJob?)null);
        jobs.FindCompletedJob("https://dev.azure.com/org", "project", "repo-gh-1", 42, 11)
            .Returns((ReviewJob?)null);

        scanRepository.GetAsync(ClientId, "repo-gh-1", 42, Arg.Any<CancellationToken>())
            .Returns(new ReviewPrScan(Guid.NewGuid(), ClientId, "repo-gh-1", 42, "revision-1"));
        threadStatusFetcher.GetReviewerThreadStatusesAsync(
                "https://dev.azure.com/org",
                "project",
                "repo-gh-1",
                42,
                ReviewerId,
                ClientId,
                Arg.Any<CancellationToken>())
            .Returns([]);

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance,
            threadStatusFetcher: threadStatusFetcher,
            prScanRepository: scanRepository);

        var outcome = await sut.SynchronizeAsync(
            CreateGitHubRequest(PullRequestActivationSource.Webhook, "pull request updated") with
            {
                CandidateIterationId = 11,
                RequestedReviewerIdentity =
                new ReviewerIdentity(
                    new ProviderHostRef(ScmProvider.GitHub, "https://github.com"),
                    ReviewerId.ToString("D"),
                    "meister-dev-bot",
                    "Meister Dev Bot",
                    true),
            });

        Assert.Equal(PullRequestSynchronizationReviewDecision.NoReviewChanges, outcome.ReviewDecision);
        await jobs.DidNotReceive().AddAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SynchronizeAsync_WithSupersededActiveProviderRevision_CancelsOlderJobBeforeQueueing()
    {
        var jobs = Substitute.For<IJobRepository>();
        var existingJob = new ReviewJob(
            Guid.NewGuid(),
            ClientId,
            "https://dev.azure.com/org",
            "project",
            "repo-gh-1",
            42,
            10)
        {
            Status = JobStatus.Processing,
        };
        var request = CreateGitHubRequest(PullRequestActivationSource.Webhook, "pull request updated") with
        {
            CandidateIterationId = 11,
        };
        var codeReview = Assert.IsType<CodeReviewRef>(request.CodeReview);

        existingJob.SetProviderReviewContext(codeReview);
        existingJob.SetReviewRevision(new ReviewRevision("old-head-sha", "base-sha", "start-sha", "revision-0", "patch-0"));

        jobs.GetActiveJobsForConfigAsync("https://dev.azure.com/org", "project", Arg.Any<CancellationToken>())
            .Returns([existingJob]);
        jobs.FindActiveJob("https://dev.azure.com/org", "project", "repo-gh-1", 42, 11)
            .Returns((ReviewJob?)null);
        jobs.FindCompletedJob("https://dev.azure.com/org", "project", "repo-gh-1", 42, 11)
            .Returns((ReviewJob?)null);

        var sut = new PullRequestSynchronizationService(
            jobs,
            NullLogger<PullRequestSynchronizationService>.Instance);

        var outcome = await sut.SynchronizeAsync(request);

        Assert.Equal(PullRequestSynchronizationReviewDecision.Submitted, outcome.ReviewDecision);
        Assert.Equal(PullRequestSynchronizationLifecycleDecision.CancelledActiveJobs, outcome.LifecycleDecision);
        Assert.Contains(
            outcome.ActionSummaries,
            summary => summary.Contains(
                "Cancelled 1 superseded active review job",
                StringComparison.OrdinalIgnoreCase));
        await jobs.Received(1).SetCancelledAsync(existingJob.Id, Arg.Any<CancellationToken>());
        await jobs.Received(1)
            .AddAsync(
                Arg.Is<ReviewJob>(job =>
                    job.Provider == ScmProvider.GitHub &&
                    job.ProviderRevisionId == "revision-1"),
                Arg.Any<CancellationToken>());
    }

    private static PullRequestSynchronizationRequest CreateGitHubRequest(
        PullRequestActivationSource activationSource,
        string summaryLabel)
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "repo-gh-1", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", "start-sha", "revision-1", "patch-1");

        return new PullRequestSynchronizationRequest
        {
            ActivationSource = activationSource,
            SummaryLabel = summaryLabel,
            ClientId = ClientId,
            ProviderScopePath = "https://dev.azure.com/org",
            ProviderProjectKey = "project",
            RepositoryId = "repo-gh-1",
            PullRequestId = 42,
            PullRequestStatus = PrStatus.Active,
            Provider = ScmProvider.GitHub,
            Host = host,
            Repository = repository,
            CodeReview = review,
            ReviewRevision = revision,
            ReviewState = CodeReviewState.Open,
            RequestedReviewerIdentity =
                new ReviewerIdentity(host, "user-1", "meister-dev-bot", "Meister Dev Bot", true),
        };
    }
}

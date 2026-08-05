// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Threads;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Application.Tests.Features.Crawling.Execution;

/// <summary>
///     The conversation is triggered on its own terms: two gates, two conditions, and no dependence on
///     whether the files were reviewed.
/// </summary>
public sealed class PullRequestSynchronizationServiceThreadPassTriggerTests
{
    private static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string ScopePath = "https://dev.azure.com/org";
    private const string ProjectKey = "project";
    private const string RepositoryId = "repo-1";
    private const int PullRequestId = 42;
    private const int IterationId = 7;

    [Fact]
    public async Task SynchronizeAsync_IncrementDeclined_StillQueuesTheThreadPass()
    {
        // The regression this whole design exists for: the guard declines the file review, and before it the
        // conversation still gets its visit.
        var harness = new Harness();
        harness.WithEngagedReviewAtAnEarlierRevision();
        harness.WithThreadWatermark("6");

        var outcome = await harness.SynchronizeAsync();

        Assert.Equal(
            PullRequestSynchronizationReviewDecision.SubsequentIncrementSkipped,
            outcome.ReviewDecision);
        Assert.Equal(PullRequestSynchronizationThreadPassDecision.Queued, outcome.ThreadPassDecision);
        Assert.NotNull(outcome.ThreadPassJobId);
        await harness.ThreadPassJobs.Received(1).TryClaimAsync(
            Arg.Is<ThreadPassJob>(job => job.PullRequestId == PullRequestId && job.RevisionKey == "7"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SynchronizeAsync_FirstSighting_QueuesBothPassesUnderSeparateIdentities()
    {
        var harness = new Harness();
        harness.WithNoScan();

        var outcome = await harness.SynchronizeAsync();

        Assert.Equal(PullRequestSynchronizationReviewDecision.Submitted, outcome.ReviewDecision);
        Assert.Equal(PullRequestSynchronizationThreadPassDecision.Queued, outcome.ThreadPassDecision);
        Assert.NotNull(outcome.JobId);
        Assert.NotNull(outcome.ThreadPassJobId);
        Assert.NotEqual(outcome.JobId, outcome.ThreadPassJobId);
    }

    [Fact]
    public async Task SynchronizeAsync_ReplyWithoutPush_QueuesTheThreadPassOnTheReplyConditionAlone()
    {
        var harness = new Harness();
        harness.WithThreadWatermark("7");
        harness.WithReviewerThread(threadId: "17", storedReplyCount: 0, observedReplyCount: 1);

        var outcome = await harness.SynchronizeAsync();

        Assert.Equal(PullRequestSynchronizationThreadPassDecision.Queued, outcome.ThreadPassDecision);
    }

    [Fact]
    public async Task SynchronizeAsync_NothingChanged_QueuesNoThreadPass()
    {
        var harness = new Harness();
        harness.WithThreadWatermark("7");
        harness.WithReviewerThread(threadId: "17", storedReplyCount: 1, observedReplyCount: 1);

        var outcome = await harness.SynchronizeAsync();

        Assert.Equal(PullRequestSynchronizationThreadPassDecision.NotDue, outcome.ThreadPassDecision);
        Assert.Null(outcome.ThreadPassJobId);
        await harness.ThreadPassJobs.DidNotReceive()
            .TryClaimAsync(Arg.Any<ThreadPassJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SynchronizeAsync_ResolutionDisabled_QueuesNoThreadPass()
    {
        var harness = new Harness();
        harness.WithCommentResolutionBehavior(CommentResolutionBehavior.Disabled);

        var outcome = await harness.SynchronizeAsync();

        Assert.Equal(PullRequestSynchronizationThreadPassDecision.ResolutionDisabled, outcome.ThreadPassDecision);
        await harness.ThreadPassJobs.DidNotReceive()
            .TryClaimAsync(Arg.Any<ThreadPassJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SynchronizeAsync_ProviderCannotWriteThreadStatus_QueuesNoThreadPass()
    {
        var harness = new Harness();
        harness.WithProviderCapabilities([ReviewThreadCapabilities.Reply]);

        var outcome = await harness.SynchronizeAsync();

        Assert.Equal(PullRequestSynchronizationThreadPassDecision.ProviderUnsupported, outcome.ThreadPassDecision);
        await harness.ThreadPassJobs.DidNotReceive()
            .TryClaimAsync(Arg.Any<ThreadPassJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SynchronizeAsync_PassAlreadyClaimed_QueuesNoSecondOne()
    {
        var harness = new Harness();
        harness.WithNoScan();
        var existing = harness.CreateExistingPass();
        harness.ThreadPassJobs.TryClaimAsync(Arg.Any<ThreadPassJob>(), Arg.Any<CancellationToken>())
            .Returns(new TryClaimThreadPassResult(false, existing));

        var outcome = await harness.SynchronizeAsync();

        Assert.Equal(PullRequestSynchronizationThreadPassDecision.AlreadyClaimed, outcome.ThreadPassDecision);
        Assert.Equal(existing.Id, outcome.ThreadPassJobId);
    }

    [Fact]
    public async Task SynchronizeAsync_TriggerEvaluationThrows_SaysSoRatherThanReportingNothingToDo()
    {
        // A failure reported as "nothing was due" is a pull request whose threads quietly stop being
        // answered, with a warning line as the only trace.
        var harness = new Harness();
        harness.WithNoScan();
        harness.WithFailingThreadStatusFetch();

        var outcome = await harness.SynchronizeAsync();

        Assert.Equal(PullRequestSynchronizationThreadPassDecision.Failed, outcome.ThreadPassDecision);
        Assert.Null(outcome.ThreadPassJobId);
        Assert.Contains(
            outcome.ActionSummaries,
            summary => summary.Contains("Could not decide", StringComparison.Ordinal));

        // The file review is unaffected: the conversation failing to be scheduled never stops it.
        Assert.Equal(PullRequestSynchronizationReviewDecision.Submitted, outcome.ReviewDecision);
    }

    [Fact]
    public async Task SynchronizeAsync_ClosedPullRequest_CancelsThreadPassWorkToo()
    {
        var harness = new Harness();
        harness.ThreadPassJobs.CancelActiveForPullRequestAsync(
                ClientId,
                RepositoryId,
                PullRequestId,
                Arg.Any<CancellationToken>())
            .Returns(2);

        var outcome = await harness.SynchronizeAsync(PrStatus.Completed);

        Assert.Equal(PullRequestSynchronizationLifecycleDecision.CancelledActiveJobs, outcome.LifecycleDecision);
        Assert.Contains(
            outcome.ActionSummaries,
            summary => summary.Contains("thread pass(es)", StringComparison.Ordinal));
        await harness.ThreadPassJobs.Received(1)
            .CancelActiveForPullRequestAsync(ClientId, RepositoryId, PullRequestId, Arg.Any<CancellationToken>());
    }

    private sealed class Harness
    {
        private readonly IJobRepository _jobs = Substitute.For<IJobRepository>();

        private readonly IReviewerThreadStatusFetcher _threadStatusFetcher =
            Substitute.For<IReviewerThreadStatusFetcher>();

        private readonly IReviewPrScanRepository _scanRepository = Substitute.For<IReviewPrScanRepository>();
        private readonly IClientRegistry _clientRegistry = Substitute.For<IClientRegistry>();
        private readonly IScmProviderRegistry _providerRegistry = Substitute.For<IScmProviderRegistry>();
        private ReviewPrScan? _scan;

        public Harness()
        {
            this._scan = new ReviewPrScan(Guid.NewGuid(), ClientId, RepositoryId, PullRequestId, "7")
            {
                LastThreadPassRevisionKey = "6",
            };

            this._clientRegistry.GetCommentResolutionBehaviorAsync(ClientId, Arg.Any<CancellationToken>())
                .Returns(CommentResolutionBehavior.Silent);
            this._clientRegistry.GetReviewEveryIncrementEnabledAsync(ClientId, Arg.Any<CancellationToken>())
                .Returns(true);
            this._providerRegistry.GetRegisteredCapabilities(ScmProvider.AzureDevOps)
                .Returns([ReviewThreadCapabilities.Status, ReviewThreadCapabilities.Reply]);

            this._jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
                .Returns(new TryAddReviewJobResult(true, null, 0));
            this._jobs.GetActiveJobsForConfigAsync(ScopePath, ProjectKey, Arg.Any<CancellationToken>())
                .Returns([]);

            this.ThreadPassJobs.TryClaimAsync(Arg.Any<ThreadPassJob>(), Arg.Any<CancellationToken>())
                .Returns(new TryClaimThreadPassResult(true, null));

            this.ApplyScan();
            this.ApplyThreads([]);
        }

        public IThreadPassJobRepository ThreadPassJobs { get; } = Substitute.For<IThreadPassJobRepository>();

        public void WithNoScan()
        {
            this._scan = null;
            this.ApplyScan();
        }

        public void WithThreadWatermark(string revisionKey)
        {
            this._scan!.LastThreadPassRevisionKey = revisionKey;
            this.ApplyScan();
        }

        public void WithReviewerThread(string threadId, int storedReplyCount, int observedReplyCount)
        {
            this._scan!.Threads.Add(
                new ReviewPrScanThread
                {
                    ReviewPrScanId = this._scan.Id,
                    ThreadId = threadId,
                    LastSeenReplyCount = storedReplyCount,
                });
            this.ApplyScan();
            this.ApplyThreads(
            [
                new PrThreadStatusEntry(threadId, "Active", "/src/file.cs", "Bot: comment", observedReplyCount),
            ]);
        }

        public void WithCommentResolutionBehavior(CommentResolutionBehavior behavior)
        {
            this._clientRegistry.GetCommentResolutionBehaviorAsync(ClientId, Arg.Any<CancellationToken>())
                .Returns(behavior);
        }

        public void WithProviderCapabilities(IReadOnlyList<string> capabilities)
        {
            this._providerRegistry.GetRegisteredCapabilities(ScmProvider.AzureDevOps).Returns(capabilities);
        }

        /// <summary>Makes reading the reviewer's threads throw, which is the trigger's own failure path.</summary>
        public void WithFailingThreadStatusFetch()
        {
            this._threadStatusFetcher.GetReviewerThreadStatusesAsync(
                    ScopePath,
                    ProjectKey,
                    RepositoryId,
                    PullRequestId,
                    Arg.Any<ThreadOwnershipResolver>(),
                    ClientId,
                    Arg.Any<CancellationToken>())
                .ThrowsAsync(new InvalidOperationException("the provider is unreachable"));
        }

        public void WithEngagedReviewAtAnEarlierRevision()
        {
            this._clientRegistry.GetReviewEveryIncrementEnabledAsync(ClientId, Arg.Any<CancellationToken>())
                .Returns(false);
            this._scan!.LastProcessedCommitId = "6";
            this.ApplyScan();
            this._jobs.GetLatestEngagedRevisionAsync(
                    ClientId,
                    ScopePath,
                    ProjectKey,
                    RepositoryId,
                    PullRequestId,
                    Arg.Any<CancellationToken>())
                .Returns(new EngagedReviewRevision("6", null, 6));
        }

        public ThreadPassJob CreateExistingPass()
        {
            return new ThreadPassJob(
                Guid.NewGuid(),
                ClientId,
                ScopePath,
                ProjectKey,
                RepositoryId,
                PullRequestId,
                IterationId,
                "7",
                "7|existing");
        }

        public Task<PullRequestSynchronizationOutcome> SynchronizeAsync(PrStatus status = PrStatus.Active)
        {
            var sut = new PullRequestSynchronizationService(
                this._jobs,
                NullLogger<PullRequestSynchronizationService>.Instance,
                Substitute.For<IPullRequestIterationResolver>(),
                this._threadStatusFetcher,
                Substitute.For<IThreadMemoryService>(),
                this._scanRepository,
                this._clientRegistry,
                threadPassJobs: this.ThreadPassJobs,
                providerRegistry: this._providerRegistry);

            return sut.SynchronizeAsync(
                new PullRequestSynchronizationRequest
                {
                    ActivationSource = PullRequestActivationSource.Crawl,
                    SummaryLabel = "crawl discovery",
                    ClientId = ClientId,
                    ProviderScopePath = ScopePath,
                    ProviderProjectKey = ProjectKey,
                    RepositoryId = RepositoryId,
                    PullRequestId = PullRequestId,
                    PullRequestStatus = status,
                    CandidateIterationId = IterationId,
                });
        }

        private void ApplyScan()
        {
            this._scanRepository.GetAsync(ClientId, RepositoryId, PullRequestId, Arg.Any<CancellationToken>())
                .Returns(this._scan);
        }

        private void ApplyThreads(IReadOnlyList<PrThreadStatusEntry> threads)
        {
            this._threadStatusFetcher.GetReviewerThreadStatusesAsync(
                    ScopePath,
                    ProjectKey,
                    RepositoryId,
                    PullRequestId,
                    Arg.Any<ThreadOwnershipResolver>(),
                    ClientId,
                    Arg.Any<CancellationToken>())
                .Returns(threads);
        }
    }
}

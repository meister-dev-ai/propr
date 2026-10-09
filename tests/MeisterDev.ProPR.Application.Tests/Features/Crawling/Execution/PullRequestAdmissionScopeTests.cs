// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterDev.ProPR.Application.Features.ReviewArchive;
using MeisterDev.ProPR.Application.Features.Reviewing.Intake.Commands.SubmitReviewByCoordinates;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using MeisterDev.ProPR.ProCursor.Contracts.Sources;

namespace MeisterDev.ProPR.Application.Tests.Features.Crawling.Execution;

public sealed class PullRequestAdmissionScopeTests
{
    [Fact]
    public async Task ManualMissingRevision_ProviderReadPrecedesAdmission()
    {
        var state = new Scenario();
        state.Query.GetReviewAsync(state.ClientId, Arg.Any<CodeReviewRef>(), Arg.Any<CancellationToken>())
            .Returns(state.Item() with { ReviewRevision = null });
        var heldDuringRead = false;
        state.Query.GetLatestRevisionAsync(state.ClientId, Arg.Any<CodeReviewRef>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                heldDuringRead = state.Held;
                return state.Revision;
            });

        Assert.Equal(SubmitReviewByCoordinatesOutcome.Submitted, (await state.Manual().HandleAsync(state.Command)).Outcome);
        Assert.False(heldDuringRead);
        Assert.Equal(1, state.Leases);
        Assert.False(state.Held);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task CrawlThreadStatus_LazyReadAndMaintenanceRetryPrecedeAdmission(bool maintenance, bool firstReadFails, bool retryFails)
    {
        var state = new Scenario();
        var statuses = Substitute.For<IReviewerThreadStatusFetcher>();
        var memory = maintenance ? Substitute.For<IThreadMemoryService>() : null;
        var scans = Substitute.For<IReviewPrScanRepository>();
        var scan = new ReviewPrScan(Guid.NewGuid(), state.ClientId, state.Config.ProviderScopePath, "owner", "native", 7, "1");
        scans.GetAsync(state.ClientId, Arg.Any<string>(), Arg.Any<string>(), "native", 7, Arg.Any<CancellationToken>()).Returns(scan);
        var reads = 0;
        var heldDuringRead = false;
        statuses.GetReviewerThreadStatusesAsync(
                Arg.Any<string>(), Arg.Any<string>(), "native", 7,
                Arg.Any<ThreadOwnershipResolver>(), state.ClientId, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<PrThreadStatusEntry>>(_ =>
            {
                heldDuringRead |= state.Held;
                if (++reads == 1 && firstReadFails)
                {
                    throw new InvalidOperationException("Synthetic thread read failure.");
                }

                if (reads == 2 && retryFails)
                {
                    throw new InvalidOperationException("Synthetic retry failure.");
                }

                return [];
            });
        var sync = state.Synchronizer(statuses, memory, scans);

        await state.Crawler(sync).CrawlAsync();

        Assert.False(heldDuringRead);
        Assert.Equal(firstReadFails ? 2 : 1, reads);
        Assert.False(state.Held);
        if (retryFails)
        {
            await state.Jobs.Received(1).TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
        }
        else
        {
            await state.Jobs.DidNotReceive().TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PreparedCompletion_MissingObservationAfterDatabaseTransitionDefersUntilNextPass(bool incrementGuard, bool newReply)
    {
        var state = new Scenario();
        var statuses = Substitute.For<IReviewerThreadStatusFetcher>();
        var scans = Substitute.For<IReviewPrScanRepository>();
        var clients = Substitute.For<IClientRegistry>();
        var reviewEveryIncrement = false;
        clients.GetReviewEveryIncrementEnabledAsync(state.ClientId, Arg.Any<CancellationToken>()).Returns(_ => reviewEveryIncrement);
        state.Jobs.GetLatestEngagedRevisionAsync(state.ClientId, Arg.Any<string>(), "owner", "native", 7, Arg.Any<CancellationToken>())
            .Returns(new EngagedReviewRevision("old", null, 1));
        ReviewJob? active = incrementGuard ? null : new ReviewJob(Guid.NewGuid(), state.ClientId, state.Config.ProviderScopePath, "owner", "native", 7, 1);
        active?.SetProviderReviewContext(state.Item().CodeReview);
        state.Jobs.FindActiveJob(state.ClientId, state.Config.ProviderScopePath, "owner", "native", 7, 1).Returns(_ => active);
        var scan = new ReviewPrScan(Guid.NewGuid(), state.ClientId, state.Config.ProviderScopePath, "owner", "native", 7, "old");
        scans.GetAsync(state.ClientId, Arg.Any<string>(), "owner", "native", 7, Arg.Any<CancellationToken>()).Returns(_ => scan);
        var reads = 0;
        statuses.GetReviewerThreadStatusesAsync(
                Arg.Any<string>(), "owner", "native", 7,
                Arg.Any<ThreadOwnershipResolver>(), state.ClientId, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Assert.False(state.Held);
                reads++;
                return newReply ? (IReadOnlyList<PrThreadStatusEntry>)[new("17", "Active", "/file.cs", "Reply", 1)] : [];
            });
        var service = new PullRequestSynchronizationService(
            MeisterDev.ProPR.TestSupport.LocalScmPolicies.Registry,
            state.Jobs, NullLogger<PullRequestSynchronizationService>.Instance,
            threadStatusFetcher: statuses, prScanRepository: scans, clientRegistry: incrementGuard ? clients : null);
        var prepared = await service.PrepareAsync(state.Request);
        Assert.Equal(0, reads);

        // A zero-finding completion removes its job and writes the matching watermark. A settings change
        // can also make change detection reachable after preparation skipped the first-only increment.
        active = null;
        reviewEveryIncrement = true;
        scan = new ReviewPrScan(Guid.NewGuid(), state.ClientId, state.Config.ProviderScopePath, "owner", "native", 7, "1");
        await using (var admission = await state.Configurations.AcquireReviewTargetAdmissionAsync(state.ClientId))
        {
            var deferred = await prepared.CompleteAsync();
            Assert.Equal(PullRequestSynchronizationReviewDecision.None, deferred.ReviewDecision);
            Assert.Null(deferred.JobId);
        }

        Assert.Equal(0, reads);
        await state.Jobs.DidNotReceive().TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());

        var next = await service.PrepareAsync(state.Request);
        Assert.Equal(1, reads);
        await using (var admission = await state.Configurations.AcquireReviewTargetAdmissionAsync(state.ClientId))
        {
            var outcome = await next.CompleteAsync();
            Assert.Equal(
                newReply ? PullRequestSynchronizationReviewDecision.Submitted : PullRequestSynchronizationReviewDecision.NoReviewChanges,
                outcome.ReviewDecision);
        }

        Assert.Equal(1, reads);
        if (newReply)
        {
            await state.Jobs.Received(1).TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
        }
        else
        {
            await state.Jobs.DidNotReceive().TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
        }
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("removed")]
    [InlineData("revision")]
    [InlineData("branch")]
    [InlineData("identity")]
    [InlineData("owner")]
    [InlineData("enabled")]
    public async Task CrawlFinalAuthorization_ReconcilesAcceptedJobOnlyAfterCurrentTargetAuthorization(string change)
    {
        var state = new Scenario();
        var statuses = Substitute.For<IReviewerThreadStatusFetcher>();
        var memory = Substitute.For<IThreadMemoryService>();
        var scans = Substitute.For<IReviewPrScanRepository>();
        var scan = new ReviewPrScan(Guid.NewGuid(), state.ClientId, state.Config.ProviderScopePath, "owner", "native", 7, "1");
        scan.Threads.Add(new ReviewPrScanThread { ReviewPrScanId = scan.Id, ThreadId = "17", LastSeenStatus = "Active" });
        scans.GetAsync(state.ClientId, Arg.Any<string>(), Arg.Any<string>(), "native", 7, Arg.Any<CancellationToken>()).Returns(scan);
        var changed = change switch
        {
            "disabled" => state.Config with { ReviewTargetLifecycle = ReviewTargetLifecycle.Disabled },
            "removed" => state.Config with { ReviewTargetLifecycle = ReviewTargetLifecycle.Removed },
            "revision" => state.Config with { ReviewTargetRevision = 2 },
            "branch" => state.Config with { RepoFilters = [state.Config.RepoFilters[0] with { TargetBranchPatterns = ["release/*"] }] },
            "identity" => state.Config with { RepoFilters = [state.Config.RepoFilters[0] with { CanonicalSourceRef = new("gitHub", "another") }] },
            "owner" => state.Config with { ClientId = Guid.NewGuid() },
            _ => state.Config,
        };
        var accepted = new ReviewJob(Guid.NewGuid(), state.ClientId, state.Config.ProviderScopePath, "owner", "native", 7, 1);
        accepted.SetReviewRevision(new ReviewRevision("old", "base", null, "old", "base...old"));
        accepted.SetProviderReviewContext(state.Item().CodeReview);
        state.Jobs.SetSupersededAsync(accepted.Id, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Assert.True(state.Held);
            accepted.Status = JobStatus.Superseded;
            return Task.CompletedTask;
        });
        state.Discovery.ListAssignedOpenReviewsAsync(Arg.Any<CrawlConfigurationDto>(), Arg.Any<CancellationToken>())
            .Returns([state.Assigned(7) with { RevisionId = 2, ReviewRevision = state.Revision }]);
        state.Jobs.GetActiveJobsForConfigAsync(state.ClientId, Arg.Any<string>(), "owner", Arg.Any<CancellationToken>()).Returns([accepted]);
        statuses.GetReviewerThreadStatusesAsync(
            Arg.Any<string>(), "owner", "native", 7,
            Arg.Any<ThreadOwnershipResolver>(), state.ClientId, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            state.Configurations.GetReviewTargetPolicySnapshotAsync(state.Config.Id, state.ClientId, Arg.Any<CancellationToken>()).Returns(changed);
            return (IReadOnlyList<PrThreadStatusEntry>)[new("17", "Fixed", "/file.cs", "Resolved", 1)];
        });

        await state.Crawler(state.Synchronizer(statuses, memory, scans)).CrawlAsync();

        await memory.Received(1).HandleThreadResolvedAsync(Arg.Any<MeisterDev.ProPR.Domain.Events.ThreadResolvedDomainEvent>(), Arg.Any<CancellationToken>());
        if (change == "enabled")
        {
            await state.Jobs.Received(1).TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
            await state.Jobs.Received(1).SetSupersededAsync(accepted.Id, Arg.Any<CancellationToken>());
            Assert.Equal(JobStatus.Superseded, accepted.Status);
        }
        else
        {
            await state.Jobs.DidNotReceive().TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
            await state.Jobs.DidNotReceive().SetSupersededAsync(accepted.Id, Arg.Any<CancellationToken>());
            Assert.Equal(JobStatus.Pending, accepted.Status);
        }

        Assert.False(state.Held);
    }

    [Fact]
    public async Task CrawlFinalAuthorization_RefusesSameRepositoryIdentityFromAnotherHost()
    {
        var state = new Scenario();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://foreign.example.test");
        var repository = new RepositoryRef(host, "native", "owner", "owner/repo", "repo");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "7", 7);
        state.Discovery.ListAssignedOpenReviewsAsync(Arg.Any<CrawlConfigurationDto>(), Arg.Any<CancellationToken>())
            .Returns([new AssignedCodeReviewRef(host, repository, review, 1, TargetBranch: "main")]);

        await state.Crawler(state.Synchronizer()).CrawlAsync();

        await state.Jobs.DidNotReceive().TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
        Assert.False(state.Held);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedCompletion_IsBoundToObservedReviewAndCanBeConsumedOnlyOnce(bool cancel)
    {
        var state = new Scenario();
        var request = state.Request;
        var prepared = await state.Synchronizer().PrepareAsync(request);
        using var cancellation = new CancellationTokenSource();
        if (cancel)
        {
            cancellation.Cancel();
        }

        var source = Guid.NewGuid();
        var settings = new PullRequestReviewSettings(ProCursorSourceScopeMode.SelectedSources, [source], [], 0.7f);

        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prepared.CompleteAsync(settings, cancellation.Token));
        }
        else
        {
            Assert.Equal(PullRequestSynchronizationReviewDecision.Submitted, (await prepared.CompleteAsync(settings)).ReviewDecision);
            await state.Jobs.Received(1).TryAddIfNoActiveDuplicateAsync(
                Arg.Is<ReviewJob>(job => job.ClientId == state.ClientId &&
                                         job.RepositoryId == "native" && job.PullRequestId == 7 && job.IterationId == 1 &&
                                         job.ProCursorSourceScopeMode == ProCursorSourceScopeMode.SelectedSources && job.ReviewTemperature == 0.7f),
                Arg.Any<CancellationToken>());
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => prepared.CompleteAsync());
        if (cancel)
        {
            await state.Jobs.DidNotReceive().TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task Preparation_ResolvesMissingIterationBeforeAnyQueueMutation()
    {
        var state = new Scenario();
        var iterations = Substitute.For<IPullRequestIterationResolver>();
        iterations.GetLatestIterationIdAsync(state.ClientId, state.Config.ProviderScopePath, "owner", "native", 7, Arg.Any<CancellationToken>()).Returns(2);
        var service = new PullRequestSynchronizationService(
            MeisterDev.ProPR.TestSupport.LocalScmPolicies.Registry, state.Jobs, NullLogger<PullRequestSynchronizationService>.Instance,
            iterationResolver: iterations);

        var prepared = await service.PrepareAsync(state.Request with { CandidateIterationId = null });

        await iterations.Received(1).GetLatestIterationIdAsync(
            state.ClientId, state.Config.ProviderScopePath, "owner", "native", 7, Arg.Any<CancellationToken>());
        await state.Jobs.DidNotReceive().TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>());
        await prepared.CompleteAsync();
        await iterations.Received(1).GetLatestIterationIdAsync(
            state.ClientId, state.Config.ProviderScopePath, "owner", "native", 7, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualCompletion_FailureOrCancellationDisposesFinalAdmission(bool cancel)
    {
        var state = new Scenario();
        state.Jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>()).Returns<TryAddReviewJobResult>(_ =>
        {
            Assert.True(state.Held);
            if (cancel)
            {
                throw new OperationCanceledException();
            }

            throw new InvalidOperationException("Synthetic persistence failure.");
        });

        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => state.Manual().HandleAsync(state.Command));
        }
        else
        {
            Assert.Equal(SubmitReviewByCoordinatesOutcome.SubmissionFailed, (await state.Manual().HandleAsync(state.Command)).Outcome);
        }

        Assert.Equal(1, state.Leases);
        Assert.Equal(1, state.Releases);
        Assert.False(state.Held);
    }

    [Fact]
    public async Task Preparation_DeclinedIncrementDoesNotFetchStatusesOrWritePendingRevisionBeforeAdmission()
    {
        var state = new Scenario();
        var clients = Substitute.For<IClientRegistry>();
        var statuses = Substitute.For<IReviewerThreadStatusFetcher>();
        var scans = Substitute.For<IReviewPrScanRepository>();
        var scan = new ReviewPrScan(Guid.NewGuid(), state.ClientId, state.Config.ProviderScopePath, "owner", "native", 7, "old");
        scans.GetAsync(state.ClientId, Arg.Any<string>(), "owner", "native", 7, Arg.Any<CancellationToken>()).Returns(scan);
        state.Jobs.GetLatestEngagedRevisionAsync(state.ClientId, Arg.Any<string>(), "owner", "native", 7, Arg.Any<CancellationToken>())
            .Returns(new EngagedReviewRevision("old", null, 1));
        var service = new PullRequestSynchronizationService(
            MeisterDev.ProPR.TestSupport.LocalScmPolicies.Registry,
            state.Jobs, NullLogger<PullRequestSynchronizationService>.Instance,
            threadStatusFetcher: statuses, prScanRepository: scans, clientRegistry: clients, prScanPendingReviewWriter: scans);

        var prepared = await service.PrepareAsync(state.Request);

        await statuses.DidNotReceiveWithAnyArgs().GetReviewerThreadStatusesAsync(default!, default!, default!, default, default!, default);
        await scans.DidNotReceiveWithAnyArgs().SetPendingReviewRevisionAsync(default, default!, default!, default!, default, default!);
        Assert.Equal(PullRequestSynchronizationReviewDecision.SubsequentIncrementSkipped, (await prepared.CompleteAsync()).ReviewDecision);
        await scans.Received(1).SetPendingReviewRevisionAsync(
            state.ClientId, state.Config.ProviderScopePath, "owner", "native", 7, "1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ManualRetainedThreadRead_PrecedesAdmissionAndRunsOnce()
    {
        var state = new Scenario();
        var connections = Substitute.For<IClientScmConnectionRepository>();
        var now = DateTimeOffset.UtcNow;
        var connection = new ClientScmConnectionDto(
            Guid.NewGuid(), state.ClientId, ScmProvider.GitHub, state.Config.ProviderScopePath,
            ScmAuthenticationKind.PersonalAccessToken, "Connection", true, "verified", now, null, null, now, now) { StoreThreads = true };
        connections.GetByClientIdAsync(state.ClientId, Arg.Any<CancellationToken>()).Returns([connection]);
        var fetcher = Substitute.For<IPullRequestFetcher>();
        var heldDuringRead = false;
        fetcher.FetchThreadsAsync(state.Config.ProviderScopePath, "owner", "native", 7, state.ClientId, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                heldDuringRead = state.Held;
                return (IReadOnlyList<PrCommentThread>)[];
            });
        var service = new PullRequestSynchronizationService(
            MeisterDev.ProPR.TestSupport.LocalScmPolicies.Registry,
            state.Jobs, NullLogger<PullRequestSynchronizationService>.Instance,
            scmConnectionRepository: connections, pullRequestFetcher: fetcher,
            reviewArchiveIngestionService: Substitute.For<IReviewArchiveIngestionService>());

        Assert.Equal(SubmitReviewByCoordinatesOutcome.Submitted, (await state.Manual(service).HandleAsync(state.Command)).Outcome);

        Assert.False(heldDuringRead);
        await fetcher.Received(1).FetchThreadsAsync(state.Config.ProviderScopePath, "owner", "native", 7, state.ClientId, Arg.Any<CancellationToken>());
        Assert.False(state.Held);
    }

    [Fact]
    public async Task CrawlBatch_AcquiresAndReleasesAdmissionForEachPullRequest()
    {
        var state = new Scenario();
        state.Discovery.ListAssignedOpenReviewsAsync(Arg.Any<CrawlConfigurationDto>(), Arg.Any<CancellationToken>())
            .Returns([state.Assigned(7), state.Assigned(8)]);
        var persisted = new List<int>();
        state.Jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Assert.True(state.Held);
                persisted.Add(call.Arg<ReviewJob>().PullRequestId);
                return new TryAddReviewJobResult(true, null, 0);
            });

        await state.Crawler(state.Synchronizer()).CrawlAsync();

        Assert.Equal(new[] { 7, 8 }, persisted);
        Assert.Equal(2, state.Leases);
        Assert.Equal(2, state.Releases);
        Assert.False(state.Held);
    }

    private sealed class Scenario
    {
        public Guid ClientId { get; } = Guid.NewGuid();
        public ICrawlConfigurationRepository Configurations { get; } = Substitute.For<ICrawlConfigurationRepository>();
        public IJobRepository Jobs { get; } = Substitute.For<IJobRepository>();
        public ICodeReviewQueryService Query { get; } = Substitute.For<ICodeReviewQueryService>();
        public IAssignedReviewDiscoveryService Discovery { get; } = Substitute.For<IAssignedReviewDiscoveryService>();
        public CrawlConfigurationDto Config { get; }
        public RepositoryRef Repository { get; }
        public ReviewRevision Revision { get; } = new("head", "base", null, "head", "base...head");
        public bool Held { get; private set; }
        public int Leases { get; private set; }
        public int Releases { get; private set; }
        public SubmitReviewByCoordinatesCommand Command => new(this.ClientId, this.Config.ProviderScopePath, "owner", "native", 7, true);

        public PullRequestSynchronizationRequest Request => new()
        {
            ActivationSource = PullRequestActivationSource.Crawl, SummaryLabel = "crawl discovery", ClientId = this.ClientId,
            ProviderScopePath = this.Config.ProviderScopePath, ProviderProjectKey = "owner", RepositoryId = "native", PullRequestId = 7,
            PullRequestStatus = PrStatus.Active, Provider = ScmProvider.GitHub, Host = this.Repository.Host,
            Repository = this.Repository, CodeReview = this.Item().CodeReview, CandidateIterationId = 1, TargetBranch = "main",
        };

        public Scenario()
        {
            this.Config = new CrawlConfigurationDto(
                Guid.NewGuid(), this.ClientId, ScmProvider.GitHub,
                "https://github.example.test", "owner", 60, true, DateTimeOffset.UnixEpoch,
                [new CrawlRepoFilterDto(Guid.NewGuid(), "repo", [], new CanonicalSourceReferenceDto("gitHub", "native"), "repo")]);
            var host = new ProviderHostRef(ScmProvider.GitHub, this.Config.ProviderScopePath);
            this.Repository = new RepositoryRef(host, "native", "owner", "owner/repo", "repo");
            this.Configurations.GetAllActiveAsync(Arg.Any<CancellationToken>()).Returns([this.Config]);
            this.Configurations.GetByClientIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()).Returns([this.Config]);
            this.Configurations.GetManagementTargetsAsync(this.ClientId, Arg.Any<CancellationToken>()).Returns([this.Config]);
            this.Configurations.GetReviewTargetPolicySnapshotAsync(this.Config.Id, this.ClientId, Arg.Any<CancellationToken>()).Returns(this.Config);
            this.Configurations.AcquireReviewTargetAdmissionAsync(this.ClientId, Arg.Any<CancellationToken>()).Returns(_ =>
            {
                Assert.False(this.Held);
                this.Held = true;
                this.Leases++;
                var lease = Substitute.For<IAsyncDisposable>();
                lease.DisposeAsync().Returns(_ =>
                {
                    this.Held = false;
                    this.Releases++;
                    return ValueTask.CompletedTask;
                });
                return lease;
            });
            this.Query.GetReviewAsync(this.ClientId, Arg.Any<CodeReviewRef>(), Arg.Any<CancellationToken>()).Returns(this.Item());
            this.Discovery.ListAssignedOpenReviewsAsync(Arg.Any<CrawlConfigurationDto>(), Arg.Any<CancellationToken>()).Returns([this.Assigned(7)]);
            this.Jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>()).Returns(new TryAddReviewJobResult(true, null, 0));
        }

        public ReviewDiscoveryItemDto Item() => new(
            ScmProvider.GitHub, this.Repository,
            new CodeReviewRef(this.Repository, CodeReviewPlatformKind.PullRequest, "7", 7), CodeReviewState.Open,
            this.Revision, null, "Review", null, "feature", "main");

        public AssignedCodeReviewRef Assigned(int number) => new(
            this.Repository.Host, this.Repository,
            new CodeReviewRef(this.Repository, CodeReviewPlatformKind.PullRequest, number.ToString(), number),
            1, "Review", "repo", "feature", "main");

        public PullRequestSynchronizationService Synchronizer(
            IReviewerThreadStatusFetcher? statuses = null,
            IThreadMemoryService? memory = null, IReviewPrScanRepository? scans = null) =>
            new(
                MeisterDev.ProPR.TestSupport.LocalScmPolicies.Registry,
                this.Jobs, NullLogger<PullRequestSynchronizationService>.Instance,
                threadStatusFetcher: statuses, threadMemoryService: memory, prScanRepository: scans);

        public SubmitReviewByCoordinatesHandler Manual(PullRequestSynchronizationService? synchronization = null)
        {
            var registry = MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute();
            registry.GetCodeReviewQueryService(ScmProvider.GitHub).Returns(this.Query);
            return new SubmitReviewByCoordinatesHandler(
                this.Configurations, Substitute.For<IWebhookConfigurationRepository>(),
                registry, synchronization ?? this.Synchronizer(), NullLogger<SubmitReviewByCoordinatesHandler>.Instance);
        }

        public PrCrawlService Crawler(PullRequestSynchronizationService synchronizer) => new(
            this.Configurations, this.Discovery,
            this.Jobs, Substitute.For<IPrStatusFetcher>(), NullLogger<PrCrawlService>.Instance,
            pullRequestSynchronizationService: synchronizer, providerRegistry: SourcePolicyRegistry());
    }

    private static IScmProviderRegistry SourcePolicyRegistry()
    {
        var registry = MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute();
        registry.GetReviewSourcePolicy(Arg.Any<ScmProvider>()).Returns(call =>
            MeisterDev.ProPR.TestSupport.ReviewSourcePolicies.Get(call.Arg<ScmProvider>()));
        return registry;
    }
}

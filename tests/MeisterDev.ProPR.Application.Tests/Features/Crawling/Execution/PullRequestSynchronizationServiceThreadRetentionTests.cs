// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterDev.ProPR.Application.Features.ReviewArchive;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Events;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.Crawling.Execution;

public sealed class PullRequestSynchronizationServiceThreadRetentionTests
{
    private static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ConnectionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ReviewerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid HumanAuthorId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public async Task SynchronizeAsync_StoreThreadsOn_IngestsTheObservedThreadAndStampsNoCommentAsProPRs()
    {
        // Runs with a configured reviewer identity and no provenance. The expectation below that neither
        // comment is ProPR's is the deliberate narrowing, not a regression: the configured reviewer says
        // which pull requests to review and is no longer an ownership input, so a thread it authored with
        // nothing recorded against it reads as human. Provenance is what makes a thread ProPR's here, as
        // the test below it shows.
        var harness = new Harness(true);

        await harness.RunAsync();

        // Thread retention must fetch threads only, never a full pull request. A full fetch would download
        // every changed file's content and diff on each crawl cycle and risk provider rate limits.
        await harness.PullRequestFetcher.Received(1).FetchThreadsAsync(
            "https://dev.azure.com/org",
            "project",
            "repo-1",
            42,
            ClientId,
            Arg.Any<CancellationToken>());
        await harness.PullRequestFetcher.DidNotReceive().FetchAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<int?>(),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<ReviewRevision?>(),
            Arg.Any<IReviewRepositoryWorkspace?>());

        await harness.IngestionService.Received(1).HandleThreadUpdatedAsync(
            Arg.Is<ThreadUpdatedEvent>(evt =>
                evt.ClientId == ClientId
                && evt.ConnectionId == ConnectionId
                && evt.RepositoryId == "repo-1"
                && evt.PullRequestId == 42
                && evt.ThreadId == "17"
                && evt.FilePath == "/src/file.ts"
                && evt.Line == 12
                && evt.Comments.Count == 2
                // Both comments are stamped human: nothing recorded either as ProPR's, and the configured
                // reviewer identity the first one carries no longer answers that question.
                && evt.Comments[0].AuthorIdentity == ReviewerId.ToString("D")
                && !evt.Comments[0].IsAiAuthored
                && evt.Comments[1].AuthorIdentity == HumanAuthorId.ToString("D")
                && !evt.Comments[1].IsAiAuthored),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SynchronizeAsync_WithRecordedProvenance_StampsItsOwnCommentWithNoIdentityAvailable()
    {
        // The reported defect. On the crawl path there is no connection and so no token identity, and with no
        // configured reviewer identity either (the common case) authorship used to come back false for
        // everything, so ProPR's own threads were harvested as human threads it had failed to raise. What
        // ProPR posted is knowable from the provider ids it recorded, and that is what decides here.
        var originStore = Substitute.For<IPostedCommentOriginStore>();
        originStore.GetJobIdsForPullRequestAsync(ClientId, "repo-1", 42, Arg.Any<CancellationToken>())
            .Returns(new List<PostedCommentOriginRow> { new("17", "100", Guid.NewGuid()) });

        var harness = new Harness(true, originStore);

        await harness.RunAsync(withReviewerIdentity: false);

        await harness.IngestionService.Received(1).HandleThreadUpdatedAsync(
            Arg.Is<ThreadUpdatedEvent>(evt =>
                // Recorded as posted by ProPR, so ProPR's, no identity comparison needed.
                evt.Comments[0].IsAiAuthored
                // And the human's comment beside it stays human.
                && !evt.Comments[1].IsAiAuthored),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SynchronizeAsync_WithNoIdentityAndNoProvenance_LeavesEveryCommentHuman()
    {
        // The honest floor: with nothing to learn from, nothing is claimed. A thread ProPR posted before its ids
        // were recorded stays misattributed here, and the harvester's own thread-id guard is what catches it.
        var harness = new Harness(true);

        await harness.RunAsync(withReviewerIdentity: false);

        await harness.IngestionService.Received(1).HandleThreadUpdatedAsync(
            Arg.Is<ThreadUpdatedEvent>(evt => evt.Comments.All(comment => !comment.IsAiAuthored)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SynchronizeAsync_TheAdapterResolvedAnIdentity_TheIngestPassDecidesWithItToo()
    {
        // The identity exists only inside the provider adapter's connection handshake, which happens earlier
        // in this same pass while thread memory is reconciled. It is contributed into the pass's resolver, so
        // the ingest below decides with it instead of falling back to provenance it does not have.
        var harness = new Harness(true, adapterIdentity: new ThreadOwnerIdentity(ReviewerId));

        await harness.RunAsync(withReviewerIdentity: false);

        await harness.IngestionService.Received(1).HandleThreadUpdatedAsync(
            Arg.Is<ThreadUpdatedEvent>(evt =>
                evt.Comments[0].IsAiAuthored
                && !evt.Comments[1].IsAiAuthored),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SynchronizeAsync_WithOriginStore_StampsOriginatingJobIdFromProvenance()
    {
        var originatingJobId = Guid.NewGuid();
        var originStore = Substitute.For<IPostedCommentOriginStore>();

        // The bot comment (thread 17, id 100) has retained provenance; the human comment (id 101) does not.
        originStore.GetJobIdsForPullRequestAsync(ClientId, "repo-1", 42, Arg.Any<CancellationToken>())
            .Returns(
                new List<PostedCommentOriginRow>
                {
                    new("17", "100", originatingJobId),
                });

        var harness = new Harness(true, originStore);

        await harness.RunAsync();

        await harness.IngestionService.Received(1).HandleThreadUpdatedAsync(
            Arg.Is<ThreadUpdatedEvent>(evt =>
                evt.Comments.Count == 2
                && evt.Comments[0].CommentId == "100"
                && evt.Comments[0].OriginatingJobId == originatingJobId
                && evt.Comments[1].CommentId == "101"
                && evt.Comments[1].OriginatingJobId == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SynchronizeAsync_WithOriginStore_StampsByCommentId_WhenCrawledThreadIdDiffers()
    {
        var originatingJobId = Guid.NewGuid();
        var originStore = Substitute.For<IPostedCommentOriginStore>();

        // GitHub/GitLab/Forgejo record the review or discussion id as the provider thread id ("review-9"), but
        // the crawler reports a different thread id (the harness PR uses thread "17"). Their comment ids are
        // globally unique within the pull request, so the bot comment (id 100) must still resolve by comment
        // id alone: the differing crawled thread id is ignored. Arranged on GitHub, because that is the regime
        // this pins and Azure DevOps is on the other side of it.
        originStore.GetJobIdsForPullRequestAsync(ClientId, "repo-1", 42, Arg.Any<CancellationToken>())
            .Returns(
                new List<PostedCommentOriginRow>
                {
                    new("review-9", "100", originatingJobId),
                });

        var harness = new Harness(true, originStore, ScmProvider.GitHub);

        await harness.RunAsync();

        await harness.IngestionService.Received(1).HandleThreadUpdatedAsync(
            Arg.Is<ThreadUpdatedEvent>(evt =>
                evt.Comments.Count == 2
                && evt.Comments[0].CommentId == "100"
                && evt.Comments[0].OriginatingJobId == originatingJobId
                && evt.Comments[1].CommentId == "101"
                && evt.Comments[1].OriginatingJobId == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SynchronizeAsync_AzureDevOpsCommentIdsRepeatPerThread_StampsOnlyTheRecordedThread()
    {
        // Azure DevOps numbers a comment within its thread, so the first comment of every thread is id 1. One
        // recorded comment must not stamp the whole pull request: here a summary ProPR posted (thread 17) is
        // recorded, and a human thread raised beside it (thread 18) opens with a comment of the same number.
        var summaryJobId = Guid.NewGuid();
        var originStore = Substitute.For<IPostedCommentOriginStore>();
        originStore.GetJobIdsForPullRequestAsync(ClientId, "repo-1", 42, Arg.Any<CancellationToken>())
            .Returns(new List<PostedCommentOriginRow> { new("17", "1", summaryJobId) });

        var publishedAt = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
        var harness = new Harness(
            true,
            originStore,
            threads:
            [
                new PrCommentThread(
                    "17",
                    null,
                    null,
                    [new PrThreadComment("Review Bot", "AI Review Summary", ReviewerId, 1, publishedAt)],
                    "Active"),
                new PrCommentThread(
                    "18",
                    "/src/file.ts",
                    12,
                    [new PrThreadComment("Jane Dev", "This looks wrong to me.", HumanAuthorId, 1, publishedAt)],
                    "Active"),
            ]);

        await harness.RunAsync(withReviewerIdentity: false);

        await harness.IngestionService.Received(1).HandleThreadUpdatedAsync(
            Arg.Is<ThreadUpdatedEvent>(evt =>
                evt.ThreadId == "17"
                && evt.Comments[0].IsAiAuthored
                && evt.Comments[0].OriginatingJobId == summaryJobId),
            Arg.Any<CancellationToken>());
        await harness.IngestionService.Received(1).HandleThreadUpdatedAsync(
            Arg.Is<ThreadUpdatedEvent>(evt =>
                evt.ThreadId == "18"
                && !evt.Comments[0].IsAiAuthored
                && evt.Comments[0].OriginatingJobId == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SynchronizeAsync_WithoutOriginStore_IngestsWithNullOriginatingJobId()
    {
        var harness = new Harness(true);

        await harness.RunAsync();

        await harness.IngestionService.Received(1).HandleThreadUpdatedAsync(
            Arg.Is<ThreadUpdatedEvent>(evt =>
                evt.Comments.Count == 2
                && evt.Comments[0].OriginatingJobId == null
                && evt.Comments[1].OriginatingJobId == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SynchronizeAsync_WhenOriginLookupThrows_StillIngestsWithNullOriginatingJobId()
    {
        var originStore = Substitute.For<IPostedCommentOriginStore>();
        originStore.GetJobIdsForPullRequestAsync(ClientId, "repo-1", 42, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<PostedCommentOriginRow>>(_ => throw new InvalidOperationException("provenance store offline"));

        var harness = new Harness(true, originStore);

        await harness.RunAsync();

        // The lookup failure is swallowed: retained threads are still ingested, just without origins.
        await harness.IngestionService.Received(1).HandleThreadUpdatedAsync(
            Arg.Is<ThreadUpdatedEvent>(evt =>
                evt.Comments.Count == 2
                && evt.Comments[0].OriginatingJobId == null
                && evt.Comments[1].OriginatingJobId == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SynchronizeAsync_StoreThreadsOff_DoesNotIngestOrFetchThreads()
    {
        var harness = new Harness(false);

        await harness.RunAsync();

        await harness.PullRequestFetcher.DidNotReceive().FetchThreadsAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());

        await harness.IngestionService.DidNotReceive()
            .HandleThreadUpdatedAsync(Arg.Any<ThreadUpdatedEvent>(), Arg.Any<CancellationToken>());
    }

    private sealed class Harness
    {
        private readonly PullRequestSynchronizationService _sut;
        private readonly ScmProvider _provider;

        public Harness(
            bool storeThreads,
            IPostedCommentOriginStore? originStore = null,
            ScmProvider provider = ScmProvider.AzureDevOps,
            IReadOnlyList<PrCommentThread>? threads = null,
            ThreadOwnerIdentity? adapterIdentity = null)
        {
            this._provider = provider;
            this.IngestionService = Substitute.For<IReviewArchiveIngestionService>();
            this.PullRequestFetcher = Substitute.For<IPullRequestFetcher>();
            var scmConnectionRepository = Substitute.For<IClientScmConnectionRepository>();
            var jobs = Substitute.For<IJobRepository>();
            var iterationResolver = Substitute.For<IPullRequestIterationResolver>();
            var threadStatusFetcher = Substitute.For<IReviewerThreadStatusFetcher>();
            var scanRepository = Substitute.For<IReviewPrScanRepository>();
            var clientRegistry = Substitute.For<IClientRegistry>();

            clientRegistry.GetDefaultReviewPipelineProfileIdAsync(ClientId, Arg.Any<CancellationToken>())
                .Returns(ReviewPipelineProfileCatalog.FileByFileBalancedProfileId);

            jobs.FindActiveJob("https://dev.azure.com/org", "project", "repo-1", 42, 7)
                .Returns((ReviewJob?)null);
            jobs.FindCompletedJob("https://dev.azure.com/org", "project", "repo-1", 42, 7)
                .Returns((ReviewJob?)null);
            jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
                .Returns(new TryAddReviewJobResult(true, null, 0));

            scmConnectionRepository.GetByClientIdAsync(ClientId, Arg.Any<CancellationToken>())
                .Returns([CreateConnection(storeThreads, provider)]);

            this.PullRequestFetcher.FetchThreadsAsync(
                    "https://dev.azure.com/org",
                    "project",
                    "repo-1",
                    42,
                    ClientId,
                    Arg.Any<CancellationToken>())
                .Returns(threads ?? CreateThreads());

            if (adapterIdentity is { } contributedIdentity)
            {
                // Stands in for a provider adapter: the pass reaches the thread-status fetch first, and the
                // real adapter contributes the account its connection authenticates as into the resolver it
                // is handed there. A scan has to exist for that fetch to happen at all.
                scanRepository.GetAsync(ClientId, Arg.Any<string>(), Arg.Any<string>(), "repo-1", 42, Arg.Any<CancellationToken>())
                    .Returns(new ReviewPrScan(Guid.NewGuid(), ClientId, "https://provider.example", "project", "repo-1", 42, "7"));
                threadStatusFetcher.GetReviewerThreadStatusesAsync(
                        "https://dev.azure.com/org",
                        "project",
                        "repo-1",
                        42,
                        Arg.Any<ThreadOwnershipResolver>(),
                        ClientId,
                        Arg.Any<CancellationToken>())
                    .Returns(call =>
                    {
                        call.Arg<ThreadOwnershipResolver>().ContributeIdentity(contributedIdentity);
                        return Task.FromResult<IReadOnlyList<PrThreadStatusEntry>>([]);
                    });
            }

            this._sut = new PullRequestSynchronizationService(
                jobs,
                NullLogger<PullRequestSynchronizationService>.Instance,
                iterationResolver,
                threadStatusFetcher,
                Substitute.For<IThreadMemoryService>(),
                scanRepository,
                clientRegistry,
                scmConnectionRepository,
                this.PullRequestFetcher,
                this.IngestionService,
                originStore);
        }

        public IReviewArchiveIngestionService IngestionService { get; }

        public IPullRequestFetcher PullRequestFetcher { get; }

        public async Task RunAsync(bool withReviewerIdentity = true)
        {
            // The host is the same authority whichever provider the pass runs against; what the provider
            // changes is how the comment ids the crawl reports relate to the recorded ones.
            var host = new ProviderHostRef(this._provider, "https://dev.azure.com/org");
            var request = new PullRequestSynchronizationRequest
            {
                ActivationSource = PullRequestActivationSource.Crawl,
                SummaryLabel = "crawl discovery",
                ClientId = ClientId,
                ProviderScopePath = "https://dev.azure.com/org",
                ProviderProjectKey = "project",
                RepositoryId = "repo-1",
                PullRequestId = 42,
                PullRequestStatus = PrStatus.Active,
                Provider = this._provider,
                Host = host,
                CandidateIterationId = 7,
                // Omitted on purpose in one test: on most installations nothing configures a reviewer identity,
                // and the account whose token posts is a different account anyway.
                RequestedReviewerIdentity = withReviewerIdentity
                    ? new ReviewerIdentity(host, ReviewerId.ToString("D"), "review-bot", "Review Bot", true)
                    : null,
            };

            await this._sut.SynchronizeAsync(request);
        }

        private static ClientScmConnectionDto CreateConnection(bool storeThreads, ScmProvider provider)
        {
            var now = DateTimeOffset.UtcNow;
            return new ClientScmConnectionDto(
                ConnectionId,
                ClientId,
                provider,
                "https://dev.azure.com/org",
                ScmAuthenticationKind.PersonalAccessToken,
                provider.ToString(),
                true,
                "verified",
                now,
                null,
                null,
                now,
                now)
            {
                StoreThreads = storeThreads,
            };
        }

        private static IReadOnlyList<PrCommentThread> CreateThreads()
        {
            var publishedAt = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
            return
            [
                new PrCommentThread(
                    "17",
                    "/src/file.ts",
                    12,
                    [
                        new PrThreadComment("Review Bot", "Bot finding", ReviewerId, 100, publishedAt),
                        new PrThreadComment("Jane Dev", "Human reply", HumanAuthorId, 101, publishedAt.AddMinutes(5)),
                    ],
                    "Active"),
            ];
        }
    }
}

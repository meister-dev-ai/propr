// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Crawling.Execution.Models;
using MeisterProPR.Application.Features.Crawling.Execution.Services;
using MeisterProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterProPR.Application.Features.ReviewArchive;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.Events;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Application.Tests.Features.Crawling.Execution;

public sealed class PullRequestSynchronizationServiceThreadRetentionTests
{
    private static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ConnectionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ReviewerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid HumanAuthorId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public async Task SynchronizeAsync_StoreThreadsOn_IngestsObservedThreadWithStampedAuthorship()
    {
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
                // The bot's comment is stamped AI-authored; the human's comment is not.
                && evt.Comments[0].AuthorIdentity == ReviewerId.ToString("D")
                && evt.Comments[0].IsAiAuthored
                && evt.Comments[1].AuthorIdentity == HumanAuthorId.ToString("D")
                && !evt.Comments[1].IsAiAuthored),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SynchronizeAsync_WithOriginStore_StampsOriginatingJobIdFromProvenance()
    {
        var originatingJobId = Guid.NewGuid();
        var originStore = Substitute.For<IPostedCommentOriginStore>();

        // The bot comment (thread 17, id 100) has retained provenance; the human comment (id 101) does not.
        // Attribution is comment-id-primary: a single origin under comment id "100" resolves outright.
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

        // GitHub/GitLab/Forgejo record the review/discussion id as the provider thread id ("review-9"), but
        // the crawler reports a different thread id (the harness PR uses thread "17"). Their comment ids are
        // globally unique within the pull request, so the bot comment (id 100) must still resolve by comment
        // id alone — the differing crawled thread id is ignored.
        originStore.GetJobIdsForPullRequestAsync(ClientId, "repo-1", 42, Arg.Any<CancellationToken>())
            .Returns(
                new List<PostedCommentOriginRow>
                {
                    new("review-9", "100", originatingJobId),
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

        public Harness(bool storeThreads, IPostedCommentOriginStore? originStore = null)
        {
            this.IngestionService = Substitute.For<IReviewArchiveIngestionService>();
            this.PullRequestFetcher = Substitute.For<IPullRequestFetcher>();
            var scmConnectionRepository = Substitute.For<IClientScmConnectionRepository>();
            var jobs = Substitute.For<IJobRepository>();
            var iterationResolver = Substitute.For<IPullRequestIterationResolver>();
            var threadStatusFetcher = Substitute.For<IReviewerThreadStatusFetcher>();
            var scanRepository = Substitute.For<IReviewPrScanRepository>();
            var clientRegistry = Substitute.For<IClientRegistry>();

            clientRegistry.GetDefaultReviewStrategyAsync(ClientId, Arg.Any<CancellationToken>())
                .Returns(ReviewStrategy.FileByFile);
            clientRegistry.GetDefaultReviewPipelineProfileIdAsync(ClientId, Arg.Any<CancellationToken>())
                .Returns(ReviewPipelineProfileCatalog.FileByFileBalancedProfileId);

            jobs.FindActiveJob("https://dev.azure.com/org", "project", "repo-1", 42, 7)
                .Returns((ReviewJob?)null);
            jobs.FindCompletedJob("https://dev.azure.com/org", "project", "repo-1", 42, 7)
                .Returns((ReviewJob?)null);
            jobs.TryAddIfNoActiveDuplicateAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
                .Returns(new TryAddReviewJobResult(true, null, 0));

            scmConnectionRepository.GetByClientIdAsync(ClientId, Arg.Any<CancellationToken>())
                .Returns([CreateConnection(storeThreads)]);

            this.PullRequestFetcher.FetchThreadsAsync(
                    "https://dev.azure.com/org",
                    "project",
                    "repo-1",
                    42,
                    ClientId,
                    Arg.Any<CancellationToken>())
                .Returns(CreateThreads());

            this._sut = new PullRequestSynchronizationService(
                jobs,
                NullLogger<PullRequestSynchronizationService>.Instance,
                iterationResolver,
                threadStatusFetcher,
                null,
                scanRepository,
                clientRegistry,
                scmConnectionRepository,
                this.PullRequestFetcher,
                this.IngestionService,
                originStore);
        }

        public IReviewArchiveIngestionService IngestionService { get; }

        public IPullRequestFetcher PullRequestFetcher { get; }

        public async Task RunAsync()
        {
            var host = new ProviderHostRef(ScmProvider.AzureDevOps, "https://dev.azure.com/org");
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
                Provider = ScmProvider.AzureDevOps,
                Host = host,
                CandidateIterationId = 7,
                RequestedReviewerIdentity = new ReviewerIdentity(
                    host,
                    ReviewerId.ToString("D"),
                    "review-bot",
                    "Review Bot",
                    true),
            };

            await this._sut.SynchronizeAsync(request);
        }

        private static ClientScmConnectionDto CreateConnection(bool storeThreads)
        {
            var now = DateTimeOffset.UtcNow;
            return new ClientScmConnectionDto(
                ConnectionId,
                ClientId,
                ScmProvider.AzureDevOps,
                "https://dev.azure.com/org",
                ScmAuthenticationKind.PersonalAccessToken,
                "Azure DevOps",
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
                    17,
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

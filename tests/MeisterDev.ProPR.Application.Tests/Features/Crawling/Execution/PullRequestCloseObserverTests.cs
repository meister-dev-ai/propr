// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Application.Features.ReviewArchive;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Events;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Application.Tests.Features.Crawling.Execution;

/// <summary>
///     The close observation both seal paths share: fetch the finished pull request's threads, decide which of
///     them are ProPR's own, and hand the rest to the miss harvester.
/// </summary>
public sealed class PullRequestCloseObserverTests
{
    private static readonly Guid ClientId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid ConnectionId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid HumanAuthorId = Guid.Parse("88888888-8888-8888-8888-888888888888");

    /// <summary>The provider thread the harness's collected finding was raised as.</summary>
    private const string FindingThreadId = "propr-thread-1";

    [Fact]
    public async Task EveryThreadIsHandedToTheHarvesterWithTheStatusTheProviderReports()
    {
        // Without the status as it stands at the close, the harvester cannot tell that a thread has settled and
        // leaves its provisional judgement in place. Two threads with different statuses, so a pass that stops
        // after the first or flattens the status is caught.
        var harness = new Harness(threadStatus: "Fixed", secondThreadStatus: "Active");

        await harness.ObserveAsync();

        await harness.Harvester.Received(1).HandleThreadObservedAsync(
            Arg.Is<ThreadUpdatedEvent>(evt =>
                evt.ThreadId == "17"
                && evt.Status == "Fixed"
                && evt.ClientId == ClientId
                && evt.PullRequestId == 42
                && evt.ConnectionId == ConnectionId),
            Arg.Any<CancellationToken>());
        await harness.Harvester.Received(1).HandleThreadObservedAsync(
            Arg.Is<ThreadUpdatedEvent>(evt =>
                evt.ThreadId == "18"
                && evt.Status == "Active"
                && evt.ClientId == ClientId
                && evt.PullRequestId == 42
                && evt.ConnectionId == ConnectionId
                && evt.Comments.Count == 1),
            Arg.Any<CancellationToken>());
        await harness.Harvester.Received(2).HandleThreadObservedAsync(
            Arg.Any<ThreadUpdatedEvent>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AThreadProPrPostedIsMarkedAsItsOwn()
    {
        // Provenance is the whole ownership answer on this path, because no provider adapter runs to contribute
        // an account identity. A thread ProPR raised that reached the harvester unmarked would be judged as
        // something ProPR failed to raise.
        var harness = new Harness(secondCommentOnFirstThread: true);
        harness.WithProvenance("17", "101");

        await harness.ObserveAsync();

        // Only the recorded comment. Marking the whole thread would turn a human reply on ProPR's thread into
        // ProPR's own words, and the harvester reads that flag per comment.
        await harness.Harvester.Received(1).HandleThreadObservedAsync(
            Arg.Is<ThreadUpdatedEvent>(evt =>
                evt.ThreadId == "17"
                && evt.Comments.Count == 2
                && evt.Comments.Any(comment => comment.CommentId == "101" && comment.IsAiAuthored)
                && evt.Comments.Any(comment => comment.CommentId == "102" && !comment.IsAiAuthored)),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("17", "999")]
    [InlineData("999", "101")]
    public async Task AThreadWhoseProvenanceDoesNotMatchStaysHumanAuthored(string threadId, string commentId)
    {
        // The recorded row has to name this comment. Matching on either half alone would let one recorded
        // comment claim a thread ProPR never posted on, which would drop a genuine miss.
        var harness = new Harness();
        harness.WithProvenance(threadId, commentId);

        await harness.ObserveAsync();

        await harness.Harvester.Received(1).HandleThreadObservedAsync(
            Arg.Is<ThreadUpdatedEvent>(evt =>
                evt.ThreadId == "17"
                && !evt.Comments.Single(comment => comment.CommentId == "101").IsAiAuthored),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task APullRequestProPrCollectedNothingForIsNotObserved()
    {
        // Harvesting a miss creates the aggregate when none exists, so observing a pull request ProPR never
        // reviewed would let one human thread manufacture a measurement with no true positives. The seal that
        // follows counts it as recall zero over a pull request the reviewer was never shown.
        var harness = new Harness(withCollectedFindings: false);

        await harness.ObserveAsync();

        await harness.Fetcher.DidNotReceive().FetchThreadsAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());
        await harness.Harvester.DidNotReceive().HandleThreadObservedAsync(
            Arg.Any<ThreadUpdatedEvent>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AClientWhoseCollectionGateIsClosedIsNotAskedAboutItsThreads()
    {
        // The gate is per client and the harvester consults it per thread, which is after the provider round
        // trip has been paid.
        var harness = new Harness(gateOpen: false);

        await harness.ObserveAsync();

        await harness.Fetcher.DidNotReceive().FetchThreadsAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());
        await harness.ReviewerThreads.DidNotReceive().GetReviewerThreadStatusesAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<ThreadOwnershipResolver>(),
            Arg.Any<Guid>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TwoActiveConnectionsOfDifferentFamiliesOnOneHostStopTheObservation()
    {
        // The family decides the comment-id regime, and the wrong regime leaves ProPR's own comments
        // unrecognised. A host match cannot settle which family is right, so nothing is observed.
        var harness = new Harness(
            connectionRows:
            [
                Harness.CreateConnection(ScmProvider.AzureDevOps, Guid.NewGuid()),
                Harness.CreateConnection(ScmProvider.GitHub, Guid.NewGuid()),
            ]);

        await harness.ObserveAsync();

        await harness.Fetcher.DidNotReceive().FetchThreadsAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AConnectionRowWithNoHostDoesNotStopTheOneThatMatches()
    {
        var harness = new Harness(
            connectionRows:
            [
                Harness.CreateConnection(ScmProvider.AzureDevOps, Guid.NewGuid(), hostBaseUrl: "   "),
                Harness.CreateConnection(),
            ]);

        await harness.ObserveAsync();

        // Naming the connection is the point: a pass that picked the blank-host row would also have harvested
        // a thread, and the family it carries decides how ProPR's own comments are recognised.
        await harness.Harvester.Received(1).HandleThreadObservedAsync(
            Arg.Is<ThreadUpdatedEvent>(evt => evt.ConnectionId == ConnectionId),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Fixed", ThreadResolutionIntent.ClaimsFix)]
    [InlineData("Closed", ThreadResolutionIntent.ClaimsFix)]
    [InlineData("WontFix", ThreadResolutionIntent.AcceptedByHuman)]
    [InlineData("ByDesign", ThreadResolutionIntent.AcceptedByHuman)]
    public async Task AFindingWhoseThreadTheCloseResolvedGetsItsOutcomeRecorded(
        string status,
        ThreadResolutionIntent expectedIntent)
    {
        // Both sides of the ratio settle at the same moment. Recording the misses without recording these
        // would leave the false negatives complete and the true positives short.
        var harness = new Harness(
            reviewerThreads:
            [
                Harness.ReviewerThread(FindingThreadId, status, ThreadAnchorCodeChange.Changed),
            ]);

        await harness.ObserveAsync();

        // The intent is what separates an acknowledgement from a claimed fix, and so which disposition the
        // finding ends up with. A pass that flattened every resolved status would satisfy a thread-id check.
        await harness.Dispositions.Received(1).HandleThreadResolvedAsync(
            Arg.Is<ThreadResolvedDomainEvent>(evt =>
                evt.ThreadId == FindingThreadId
                && evt.ClientId == ClientId
                && evt.PullRequestId == 42
                && evt.Intent == expectedIntent
                && evt.CodeChangedSinceRaised == ThreadAnchorCodeChange.Changed
                && evt.OrganizationUrl == "https://dev.azure.com/org"
                && evt.ProjectId == "project"
                && evt.RepositoryId == "repo-1"
                && evt.FilePath == "/src/Service.cs"
                && evt.CommentHistory == "propr: A finding ProPR raised."),
            Arg.Any<CancellationToken>());

        // A predicate-scoped count says nothing about calls that do not match it.
        await harness.Dispositions.Received(1).HandleThreadResolvedAsync(
            Arg.Any<ThreadResolvedDomainEvent>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AResolvedThreadThatIsNotOneOfOurFindingsGetsNoOutcome()
    {
        // Correlation is the point of the previous test. Without this one it would pass for an implementation
        // that recorded an outcome against every resolved thread the provider named.
        var harness = new Harness(
            reviewerThreads:
            [
                Harness.ReviewerThread("someone-elses-thread", "Fixed", ThreadAnchorCodeChange.Changed),
            ]);

        await harness.ObserveAsync();

        await harness.Dispositions.DidNotReceive().HandleThreadResolvedAsync(
            Arg.Any<ThreadResolvedDomainEvent>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFindingWhoseThreadIsStillOpenGetsNoOutcome()
    {
        // An open thread has no outcome to record, and inventing one would decide a finding nobody resolved.
        var harness = new Harness(
            reviewerThreads:
            [
                Harness.ReviewerThread(FindingThreadId, "Active"),
            ]);

        await harness.ObserveAsync();

        await harness.Dispositions.DidNotReceive().HandleThreadResolvedAsync(
            Arg.Any<ThreadResolvedDomainEvent>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProPrsOwnThreadsAreReadBeforeTheHumanOnesSoOwnershipIsSettledFirst()
    {
        // The provider adapter contributes the account ProPR posts under while answering the status read. A
        // harvest that ran first would decide authorship on provenance alone.
        var harness = new Harness(
            reviewerThreads:
            [
                Harness.ReviewerThread(FindingThreadId, "Fixed"),
            ]);

        await harness.ObserveAsync();

        // Both reads are addressed as well as ordered: a pass that asked the right questions of the wrong
        // pull request would otherwise satisfy this.
        Received.InOrder(() =>
        {
            harness.ReviewerThreads.GetReviewerThreadStatusesAsync(
                "https://dev.azure.com/org",
                "project",
                "repo-1",
                42,
                Arg.Any<ThreadOwnershipResolver>(),
                ClientId,
                Arg.Any<CancellationToken>());
            harness.Fetcher.FetchThreadsAsync(
                "https://dev.azure.com/org",
                "project",
                "repo-1",
                42,
                ClientId,
                Arg.Any<CancellationToken>());
        });

        // The status read happens once, and it is the call the provider adapter contributes the account into.
        // What that contribution then does for the harvest is not observable from here, because the substitute
        // adapter contributes nothing; asserting it from this side would only restate the capture.
        Assert.Single(harness.ReviewerThreads.ReceivedCalls());
    }

    [Fact]
    public async Task AFailingStatusReadStillLeavesTheMissesHarvested()
    {
        // The two sides are independent: losing ProPR's own threads costs the outcomes, not the misses.
        var harness = new Harness();
        harness.ReviewerThreads
            .GetReviewerThreadStatusesAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<ThreadOwnershipResolver>(),
                Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the provider is unreachable"));

        await harness.ObserveAsync();

        await harness.Harvester.Received(1).HandleThreadObservedAsync(
            Arg.Any<ThreadUpdatedEvent>(),
            Arg.Any<CancellationToken>());
        await harness.Dispositions.DidNotReceive().HandleThreadResolvedAsync(
            Arg.Any<ThreadResolvedDomainEvent>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithoutADispositionServiceTheMissSideStillRuns()
    {
        var harness = new Harness(
            withDispositionService: false,
            reviewerThreads: [Harness.ReviewerThread(FindingThreadId, "Fixed")]);

        await harness.ObserveAsync();

        await harness.Harvester.Received(1).HandleThreadObservedAsync(
            Arg.Any<ThreadUpdatedEvent>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnUnresolvableConnectionStopsTheObservationBeforeTheFetch()
    {
        // The connection carries the provider family, which decides how a recorded comment is matched back to a
        // thread. Observing under a guessed family would leave ProPR's own comments unrecognised.
        var harness = new Harness(withConnection: false);

        await harness.ObserveAsync();

        await harness.Fetcher.DidNotReceive().FetchThreadsAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());
        await harness.Harvester.DidNotReceive().HandleThreadObservedAsync(
            Arg.Any<ThreadUpdatedEvent>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ACredentialInTheScopePathNeverReachesTheLog()
    {
        // The unresolved-connection branch names the host it could not match. Naming the scope path it came
        // from instead would persist whatever credential that path carried.
        List<string> logged = [];
        var harness = new Harness(withConnection: false, logger: new CapturingLogger<PullRequestCloseObserver>(logged));

        await harness.ObserveAsync(scopePath: "https://svc:s3cr3t@dev.azure.com/org");

        Assert.NotEmpty(logged);
        Assert.All(logged, line => Assert.DoesNotContain("s3cr3t", line, StringComparison.Ordinal));
        Assert.All(logged, line => Assert.DoesNotContain("svc:", line, StringComparison.Ordinal));
        Assert.Contains(logged, line => line.Contains("https://dev.azure.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AScopePathThatNamesNoHostStopsTheObservationWithoutThrowing()
    {
        var harness = new Harness();

        await harness.ObserveAsync(scopePath: "not-an-absolute-url");

        await harness.Fetcher.DidNotReceive().FetchThreadsAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithoutAHarvesterNoThreadIsFetched()
    {
        // An installation that collects no code insights must not pay a provider round trip per closed pull
        // request for an observation nothing consumes.
        var harness = new Harness(withHarvester: false);

        await harness.ObserveAsync();

        await harness.Fetcher.DidNotReceive().FetchThreadsAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFailingFetchIsSwallowedSoTheCallerCanStillSeal()
    {
        var harness = new Harness();
        harness.Fetcher
            .FetchThreadsAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the provider is unreachable"));

        await harness.ObserveAsync();

        await harness.Harvester.DidNotReceive().HandleThreadObservedAsync(
            Arg.Any<ThreadUpdatedEvent>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ACancelledObservationPropagatesSoTheCallerStops()
    {
        // Cancellation is a shutdown, not a provider fault. Swallowing it here would let the pass carry on
        // sealing against judgements it has just been told to stop gathering.
        var harness = new Harness();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        harness.Fetcher
            .FetchThreadsAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(call => new OperationCanceledException((CancellationToken)call[5]));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.ObserveAsync(ct: cancelled.Token));

        // The rethrow only means what it says if the observation was running under the caller's token. A pass
        // that fetched with CancellationToken.None would raise the same exception for a different reason.
        await harness.Fetcher.Received(1).FetchThreadsAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<Guid?>(),
            cancelled.Token);
    }

    [Fact]
    public async Task ACancellationRaisedByADependencyUnderAnotherTokenIsTreatedAsAFailure()
    {
        // The observer's own token decides whether a cancellation is ours. One raised by a dependency under a
        // token the caller never passed is a dependency fault, and the close still seals from what is recorded.
        var harness = new Harness();
        using var unrelated = new CancellationTokenSource();
        await unrelated.CancelAsync();

        harness.Fetcher
            .FetchThreadsAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException(unrelated.Token));

        await harness.ObserveAsync();

        await harness.Harvester.DidNotReceive().HandleThreadObservedAsync(
            Arg.Any<ThreadUpdatedEvent>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>Keeps the rendered message, which is where a leaked secret would show up.</summary>
    private sealed class CapturingLogger<T>(List<string> messages) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            messages.Add(formatter(state, exception));
        }
    }

    private sealed class Harness
    {
        private readonly PullRequestCloseObserver _sut;
        private readonly IPostedCommentOriginStore _origins;

        public Harness(
            bool withHarvester = true,
            bool withConnection = true,
            bool withCollectedFindings = true,
            bool gateOpen = true,
            IReadOnlyList<ClientScmConnectionDto>? connectionRows = null,
            string threadStatus = "Active",
            string? secondThreadStatus = null,
            bool secondCommentOnFirstThread = false,
            IReadOnlyList<PrThreadStatusEntry>? reviewerThreads = null,
            bool withReviewerThreadStatuses = true,
            bool withDispositionService = true,
            ILogger<PullRequestCloseObserver>? logger = null)
        {
            this.Harvester = Substitute.For<ICodeInsightMissHarvester>();
            this.Fetcher = Substitute.For<IPullRequestFetcher>();
            this._origins = Substitute.For<IPostedCommentOriginStore>();

            var connections = Substitute.For<IClientScmConnectionRepository>();
            connections.GetByClientIdAsync(ClientId, Arg.Any<CancellationToken>())
                .Returns(connectionRows ?? (withConnection ? [CreateConnection()] : []));

            this.Fetcher.FetchThreadsAsync(
                    "https://dev.azure.com/org",
                    "project",
                    "repo-1",
                    42,
                    ClientId,
                    Arg.Any<CancellationToken>())
                .Returns(CreateThreads(threadStatus, secondThreadStatus, secondCommentOnFirstThread));

            this._origins.GetJobIdsForPullRequestAsync(ClientId, "repo-1", 42, Arg.Any<CancellationToken>())
                .Returns([]);

            this.FindingStore = Substitute.For<ICodeInsightFindingStore>();
            this.FindingStore
                .GetFindingsForPullRequestAsync(Arg.Any<CodeInsightPullRequestKey>(), Arg.Any<CancellationToken>())
                .Returns(withCollectedFindings ? [CreateFinding()] : []);

            var gate = Substitute.For<ICodeInsightsCollectionGate>();
            gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(gateOpen);

            this.Dispositions = Substitute.For<ICodeInsightDispositionService>();
            this.ReviewerThreads = Substitute.For<IReviewerThreadStatusFetcher>();
            this.ReviewerThreads
                .GetReviewerThreadStatusesAsync(
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<int>(),
                    Arg.Any<ThreadOwnershipResolver>(),
                    Arg.Any<Guid>(),
                    Arg.Any<CancellationToken>())
                .Returns(reviewerThreads ?? []);

            this._sut = new PullRequestCloseObserver(
                logger ?? NullLogger<PullRequestCloseObserver>.Instance,
                this.Fetcher,
                withHarvester ? this.Harvester : null,
                this.FindingStore,
                gate,
                connections,
                this._origins,
                withReviewerThreadStatuses ? this.ReviewerThreads : null,
                withDispositionService ? this.Dispositions : null);
        }

        public ICodeInsightMissHarvester Harvester { get; }

        public IPullRequestFetcher Fetcher { get; }

        public ICodeInsightFindingStore FindingStore { get; }

        public ICodeInsightDispositionService Dispositions { get; }

        public IReviewerThreadStatusFetcher ReviewerThreads { get; }

        public static PrThreadStatusEntry ReviewerThread(
            string threadId,
            string status,
            ThreadAnchorCodeChange codeChange = ThreadAnchorCodeChange.Unknown)
        {
            return new PrThreadStatusEntry(
                threadId,
                status,
                "/src/Service.cs",
                "propr: A finding ProPR raised.",
                0,
                codeChange);
        }

        public void WithProvenance(string threadId, string commentId)
        {
            this._origins.GetJobIdsForPullRequestAsync(ClientId, "repo-1", 42, Arg.Any<CancellationToken>())
                .Returns([new PostedCommentOriginRow(threadId, commentId, Guid.NewGuid())]);
        }

        public Task ObserveAsync(string scopePath = "https://dev.azure.com/org", CancellationToken ct = default)
        {
            return this._sut.ObserveAsync(
                new CodeInsightPullRequestKey(ClientId, "repo-1", 42),
                scopePath,
                "project",
                ct);
        }

        public static ClientScmConnectionDto CreateConnection(
            ScmProvider provider = ScmProvider.AzureDevOps,
            Guid? id = null,
            string hostBaseUrl = "https://dev.azure.com/org")
        {
            var now = DateTimeOffset.UtcNow;
            return new ClientScmConnectionDto(
                id ?? ConnectionId,
                ClientId,
                provider,
                hostBaseUrl,
                ScmAuthenticationKind.PersonalAccessToken,
                provider.ToString(),
                true,
                "verified",
                now,
                null,
                null,
                now,
                now);
        }

        private static CodeInsightFindingView CreateFinding()
        {
            return new CodeInsightFindingView(
                Guid.CreateVersion7(),
                Guid.NewGuid(),
                "rev-1",
                0,
                "src/Service.cs",
                7,
                CommentSeverity.Error,
                "A finding ProPR raised on this pull request.",
                "propr-thread-1",
                DateTimeOffset.UtcNow);
        }

        private static IReadOnlyList<PrCommentThread> CreateThreads(
            string status,
            string? secondStatus = null,
            bool secondCommentOnFirstThread = false)
        {
            var publishedAt = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
            List<PrThreadComment> firstThreadComments =
            [
                new PrThreadComment(
                    "Jane Dev",
                    "This retry loop swallows the cancellation token, so a shutdown hangs.",
                    HumanAuthorId,
                    101,
                    publishedAt),
            ];

            if (secondCommentOnFirstThread)
            {
                firstThreadComments.Add(
                    new PrThreadComment(
                        "Sam Reviewer",
                        "Agreed, and the same applies one call up.",
                        HumanAuthorId,
                        102,
                        publishedAt.AddMinutes(2)));
            }

            List<PrCommentThread> threads =
            [
                new PrCommentThread("17", "/src/file.ts", 12, firstThreadComments, status),
            ];

            if (secondStatus is not null)
            {
                threads.Add(
                    new PrCommentThread(
                        "18",
                        "/src/other.ts",
                        30,
                        [
                            new PrThreadComment(
                                "Sam Reviewer",
                                "This allocates inside the loop for every row.",
                                HumanAuthorId,
                                201,
                                publishedAt.AddMinutes(5)),
                        ],
                        secondStatus));
            }

            return threads;
        }
    }
}

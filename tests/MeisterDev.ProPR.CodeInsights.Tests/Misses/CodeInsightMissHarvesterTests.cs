// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Events;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.CodeInsights.Misses;

namespace MeisterDev.ProPR.CodeInsights.Tests.Misses;

public sealed class CodeInsightMissHarvesterTests
{
    private static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task AHumanThreadPassingAllThreeJudgements_CountsAsAMiss()
    {
        var harness = new Harness();

        await harness.Harvester.HandleThreadObservedAsync(HumanThread());

        await harness.Misses.Received(1).RecordMissAsync(
            Arg.Any<CodeInsightPullRequestKey>(),
            Arg.Is<CodeInsightMissRecord>(miss =>
                miss.CountsAsMiss
                && miss.IsSubstantive
                && miss.WasActedOn
                && miss.IsInScope
                && miss.ProviderThreadId == "thread-9"
                && miss.ClassifierVersion == "test-miss"),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task EachJudgementGatesIndependently(bool substantive, bool actedOn, bool inScope)
    {
        // Recorded either way (the ones that did not qualify are what make the cut-off inspectable) but only
        // a thread that passes all three counts toward recall.
        var harness = new Harness();
        harness.WithJudgement(substantive, actedOn, inScope);

        await harness.Harvester.HandleThreadObservedAsync(HumanThread());

        await harness.Misses.Received(1).RecordMissAsync(
            Arg.Any<CodeInsightPullRequestKey>(),
            Arg.Is<CodeInsightMissRecord>(miss =>
                !miss.CountsAsMiss
                && miss.IsSubstantive == substantive
                && miss.WasActedOn == actedOn
                && miss.IsInScope == inScope),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AThreadTheAiTookPartIn_IsNotACandidateAtAll()
    {
        // That is a ProPR thread, whose outcome the disposition path records; treating it as a human miss
        // would count the reviewer's own finding against it.
        var harness = new Harness();

        await harness.Harvester.HandleThreadObservedAsync(HumanThread(includeAiComment: true));

        await harness.Classifier.DidNotReceive()
            .JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
        await harness.Misses.DidNotReceive().RecordMissAsync(
            Arg.Any<CodeInsightPullRequestKey>(),
            Arg.Any<CodeInsightMissRecord>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AThreadThatRestatesAProPrFinding_IsNotAMissAndCostsNoModelCall()
    {
        // The same issue must never be counted as both a true positive and a false negative. The check runs
        // before the judgement, so a duplicate costs nothing to reject.
        var harness = new Harness();
        harness.WithFindings(
            new CodeInsightFindingView(
                Guid.CreateVersion7(),
                Guid.NewGuid(),
                "rev-1",
                0,
                "src/Service.cs",
                42,
                CommentSeverity.Error,
                "The `user` parameter is dereferenced without a null check and will throw for an anonymous caller.",
                "thread-1",
                DateTimeOffset.UtcNow));

        await harness.Harvester.HandleThreadObservedAsync(
            HumanThread(text: "user is dereferenced here without a null check: this will throw for an anonymous caller."));

        await harness.Classifier.DidNotReceive()
            .JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
        await harness.Misses.DidNotReceive().RecordMissAsync(
            Arg.Any<CodeInsightPullRequestKey>(),
            Arg.Any<CodeInsightMissRecord>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AThreadProPrPostedAFindingAs_IsNotAMissWhateverItsCommentsClaimAboutAuthorship()
    {
        // The reported defect: authorship was decided from the configured reviewer identity, which is not
        // necessarily the account whose token posts, so ProPR's own threads arrived looking human, and a human
        // thread ProPR did not raise is by definition a miss. The thread id it posted under settles it, and the
        // text of the finding never has to match.
        var harness = new Harness();
        harness.WithFindings(
            new CodeInsightFindingView(
                Guid.CreateVersion7(),
                Guid.NewGuid(),
                "rev-1",
                0,
                "src/Service.cs",
                42,
                CommentSeverity.Error,
                "Something else entirely, so no text overlap can save us here.",
                "thread-9",
                DateTimeOffset.UtcNow));

        await harness.Harvester.HandleThreadObservedAsync(HumanThread());

        await harness.Classifier.DidNotReceive()
            .JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
        await harness.Misses.DidNotReceive().RecordMissAsync(
            Arg.Any<CodeInsightPullRequestKey>(),
            Arg.Any<CodeInsightMissRecord>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AThreadOnAFindinglessPullRequestIsStillHarvestedNormally()
    {
        // The identity guard must not swallow real human threads: no finding, no thread id to match.
        var harness = new Harness();

        await harness.Harvester.HandleThreadObservedAsync(HumanThread());

        await harness.Misses.Received(1).RecordMissAsync(
            Arg.Any<CodeInsightPullRequestKey>(),
            Arg.Is<CodeInsightMissRecord>(record => record.CountsAsMiss),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnAlreadyHarvestedThread_IsSkippedBeforeAnythingElse()
    {
        var harness = new Harness();
        harness.Misses
            .HasHarvestedThreadAsync(
                Arg.Any<CodeInsightPullRequestKey>(),
                "thread-9",
                Arg.Any<CancellationToken>())
            .Returns(true);

        await harness.Harvester.HandleThreadObservedAsync(HumanThread());

        // A crawl re-observes the same thread on every pass; harvesting it twice would double its
        // contribution to recall.
        await harness.Classifier.DidNotReceive()
            .JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
        await harness.Misses.DidNotReceive().RecordMissAsync(
            Arg.Any<CodeInsightPullRequestKey>(),
            Arg.Any<CodeInsightMissRecord>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnUnjudgeableThread_RecordsNothing()
    {
        // A harvested miss carrying invented judgements would be worse than none: it would appear in recall
        // as evidence.
        var harness = new Harness();
        harness.Classifier
            .JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>())
            .Returns((HumanMissJudgement?)null);

        await harness.Harvester.HandleThreadObservedAsync(HumanThread());

        await harness.Misses.DidNotReceive().RecordMissAsync(
            Arg.Any<CodeInsightPullRequestKey>(),
            Arg.Any<CodeInsightMissRecord>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithTheGateClosed_NothingIsReadJudgedOrRecorded()
    {
        var harness = new Harness();
        harness.Gate.IsCollectionEnabledAsync(ClientId, Arg.Any<CancellationToken>()).Returns(false);

        await harness.Harvester.HandleThreadObservedAsync(HumanThread());

        await harness.Misses.DidNotReceive().HasHarvestedThreadAsync(
            Arg.Any<CodeInsightPullRequestKey>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await harness.Classifier.DidNotReceive()
            .JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
        await harness.Misses.DidNotReceive().RecordMissAsync(
            Arg.Any<CodeInsightPullRequestKey>(),
            Arg.Any<CodeInsightMissRecord>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AThreadWhoseCommentsAreAllBlankIsIgnored()
    {
        // There is nothing to judge, so nothing is paid for. A thread with any real text in it does get
        // judged, even if one of its comments is blank.
        var harness = new Harness();

        await harness.Harvester.HandleThreadObservedAsync(BlankThread());

        await harness.Classifier.DidNotReceive()
            .JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AThreadWithNoCommentsAtAllIsIgnored()
    {
        var harness = new Harness();

        await harness.Harvester.HandleThreadObservedAsync(BlankThread(withComments: false));

        await harness.Classifier.DidNotReceive()
            .JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheJudgementSeesTheWholeDiscussionWithAuthorsAndTheResolvedStatus()
    {
        var harness = new Harness();

        await harness.Harvester.HandleThreadObservedAsync(HumanThread(status: "fixed"));

        await harness.Classifier.Received(1).JudgeAsync(
            Arg.Is<HumanMissJudgementRequest>(request =>
                request.ClientId == ClientId
                && request.ProviderThreadId == "thread-9"
                && request.FilePath == "src/Service.cs"
                && request.ThreadResolved
                && request.Discussion.Contains("alice:", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnOpenThreadIsReportedAsUnresolved()
    {
        var harness = new Harness();

        await harness.Harvester.HandleThreadObservedAsync(HumanThread(status: "active"));

        await harness.Classifier.Received(1).JudgeAsync(
            Arg.Is<HumanMissJudgementRequest>(request => !request.ThreadResolved),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFailureAnywhereIsSwallowedSoTheCrawlIsUnaffected()
    {
        var harness = new Harness();
        harness.Store
            .GetFindingsForPullRequestAsync(
                Arg.Any<CodeInsightPullRequestKey>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the database is unreachable"));

        await harness.Harvester.HandleThreadObservedAsync(HumanThread());
    }

    private static ThreadUpdatedEvent BlankThread(bool withComments = true)
    {
        List<ThreadUpdatedComment> comments = withComments
            ? [new ThreadUpdatedComment("c1", "alice", false, DateTimeOffset.UtcNow, "   ")]
            : [];

        return new ThreadUpdatedEvent(
            ClientId,
            Guid.NewGuid(),
            "repo-1",
            7,
            "thread-9",
            "src/Service.cs",
            42,
            "active",
            DateTimeOffset.UtcNow,
            comments);
    }

    [Fact]
    public async Task AThreadOfProviderActivityIsNotSomethingAHumanRaised()
    {
        // "Andreas Rain added Meister ProPR as a reviewer" arrives through the same comments API as a reply.
        // Counting it as a miss charges the reviewer for failing to raise an audit entry.
        var harness = new Harness();

        await harness.Harvester.HandleThreadObservedAsync(SystemActivityThread());

        await harness.Classifier.DidNotReceive()
            .JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
        await harness.Misses.DidNotReceive().RecordMissAsync(
            Arg.Any<CodeInsightPullRequestKey>(),
            Arg.Any<CodeInsightMissRecord>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnActivityEntryOnAHumanThreadIsLeftOutOfWhatTheModelJudges()
    {
        // The thread still counts, because a person did write on it. What the provider added to it is not part
        // of what they said, and passing it to the model would have it judge an audit entry as a review remark.
        var harness = new Harness();

        await harness.Harvester.HandleThreadObservedAsync(HumanThread(includeSystemComment: true));

        await harness.Classifier.Received(1).JudgeAsync(
            Arg.Is<HumanMissJudgementRequest>(request => !request.Discussion.Contains("added a reviewer")),
            Arg.Any<CancellationToken>());
    }

    private static ThreadUpdatedEvent SystemActivityThread()
    {
        return HumanThread() with
        {
            Comments =
            [
                new ThreadUpdatedComment(
                    "c1",
                    "00000002-0000-8888-8000-000000000000",
                    false,
                    DateTimeOffset.UtcNow.AddMinutes(-5),
                    "Andreas Rain added Meister ProPR as a reviewer",
                    null,
                    IsSystemGenerated: true),
            ],
        };
    }

    private static ThreadUpdatedEvent HumanThread(
        string text = "This drops the retry count silently, so a transient failure now fails the whole batch.",
        bool includeAiComment = false,
        bool includeSystemComment = false,
        string status = "active")
    {
        var comments = new List<ThreadUpdatedComment>
        {
            new("c1", "alice", false, DateTimeOffset.UtcNow.AddMinutes(-5), text),
            new("c2", "bob", false, DateTimeOffset.UtcNow.AddMinutes(-4), "Good catch, fixed."),
        };

        if (includeAiComment)
        {
            comments.Insert(
                0,
                new ThreadUpdatedComment("c0", "propr-bot", true, DateTimeOffset.UtcNow.AddMinutes(-6), "AI finding"));
        }

        if (includeSystemComment)
        {
            comments.Add(
                new ThreadUpdatedComment(
                    "c3",
                    "00000002-0000-8888-8000-000000000000",
                    false,
                    DateTimeOffset.UtcNow.AddMinutes(-3),
                    "Andreas Rain added a reviewer",
                    null,
                    IsSystemGenerated: true));
        }

        return new ThreadUpdatedEvent(
            ClientId,
            Guid.NewGuid(),
            "repo-1",
            7,
            "thread-9",
            "src/Service.cs",
            42,
            status,
            DateTimeOffset.UtcNow,
            comments);
    }

    private sealed class Harness
    {
        public Harness()
        {
            this.Store = Substitute.For<ICodeInsightFindingStore>();
            this.Misses = Substitute.For<ICodeInsightMissStore>();
            this.Classifier = Substitute.For<IHumanMissClassifier>();
            this.Gate = Substitute.For<ICodeInsightsCollectionGate>();

            this.Gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
            this.Classifier.ClassifierVersion.Returns("test-miss");
            this.WithJudgement(true, true, true);
            this.WithFindings();
            this.Misses
                .HasHarvestedThreadAsync(
                    Arg.Any<CodeInsightPullRequestKey>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>())
                .Returns(false);
            this.Misses
                .RecordMissAsync(
                    Arg.Any<CodeInsightPullRequestKey>(),
                    Arg.Any<CodeInsightMissRecord>(),
                    Arg.Any<CancellationToken>())
                .Returns(true);

            this.Harvester = new CodeInsightMissHarvester(
                this.Store,
                this.Misses,
                this.Classifier,
                this.Gate,
                NullLogger<CodeInsightMissHarvester>.Instance);
        }

        public ICodeInsightFindingStore Store { get; }

        public ICodeInsightMissStore Misses { get; }

        public IHumanMissClassifier Classifier { get; }

        public ICodeInsightsCollectionGate Gate { get; }

        public CodeInsightMissHarvester Harvester { get; }

        public void WithJudgement(bool substantive, bool actedOn, bool inScope)
        {
            this.Classifier
                .JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>())
                .Returns(new HumanMissJudgement(substantive, actedOn, inScope, 0.8, "because"));
        }

        public void WithFindings(params CodeInsightFindingView[] findings)
        {
            this.Store
                .GetFindingsForPullRequestAsync(
                    Arg.Any<CodeInsightPullRequestKey>(),
                    Arg.Any<CancellationToken>())
                .Returns(findings);
        }
    }
}

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
    public async Task KnownProviderHumanThreadCannotBeSuppressedByAnUnknownHistoricalFindingThread()
    {
        var harness = new Harness();
        harness.Store.ResolveProviderScopeAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns("AzureDevOps:https://dev.azure.com/other-org");
        harness.WithFindings(
            new CodeInsightFindingView(
                Guid.NewGuid(), Guid.NewGuid(), "old", 0, "different.cs", 2, CommentSeverity.Error, "Different concern", "thread-9", DateTimeOffset.UtcNow));
        await harness.Harvester.HandleThreadObservedAsync(HumanThread());
        await harness.Misses.Received(1).RecordMissAsync(Arg.Any<CodeInsightPullRequestKey>(), Arg.Any<CodeInsightMissRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DroppedEligibleMissObservationReportsIncompleteRetention()
    {
        var harness = new Harness();
        harness.Misses.RecordMissAsync(Arg.Any<CodeInsightPullRequestKey>(), Arg.Any<CodeInsightMissRecord>(), Arg.Any<CancellationToken>()).Returns(false);
        var completion = harness.Harvester.HandleThreadObservedAsync(HumanThread());
        await completion;
        Assert.False(await Assert.IsAssignableFrom<Task<bool>>(completion));
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
    public async Task AThreadIdFromAnotherProviderScopeDoesNotSuppressAHumanMiss()
    {
        var harness = new Harness();
        harness.Store.ResolveProviderScopeAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns("GitHub:https://host-a.example");
        harness.WithFindings(
            new CodeInsightFindingView(
                Guid.NewGuid(), Guid.NewGuid(), "rev-1", 0, "src/Service.cs", 42, CommentSeverity.Error, "Different concern", "thread-9", DateTimeOffset.UtcNow,
                ProviderScope: "GitHub:https://host-b.example"));
        await harness.Harvester.HandleThreadObservedAsync(HumanThread());
        await harness.Misses.Received(1).RecordMissAsync(
            Arg.Any<CodeInsightPullRequestKey>(), Arg.Is<CodeInsightMissRecord>(record => record.CountsAsMiss), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AResolvedThreadWithChangedSourceIsRejudgedWhenItReopens()
    {
        var harness = new Harness();
        harness.WithStoredJudgement(true);
        harness.Misses.GetObservationAsync(
                Arg.Any<CodeInsightPullRequestKey>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>(), Arg.Any<string?>())
            .Returns(new CodeInsightMissObservation("earlier source", false, 0));
        await harness.Harvester.HandleThreadObservedAsync(HumanThread(status: "Active"));
        await harness.Misses.Received(1).RejudgeMissAsync(
            Arg.Any<CodeInsightPullRequestKey>(), Arg.Is<CodeInsightMissRecord>(record => !record.JudgedThreadResolved), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnAlreadyHarvestedThread_IsSkippedBeforeAnythingElse()
    {
        var harness = new Harness();
        harness.WithStoredJudgement(threadResolved: true);

        await harness.Harvester.HandleThreadObservedAsync(HumanThread(status: "fixed"));

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
    public async Task AThreadStillOpen_HasTheStateItWasJudgedAgainstRecordedWithTheVerdict()
    {
        // The verdict is the model's to give: a concern can be accepted in an open thread, so an open thread is
        // not forced to a particular answer. What is recorded beside the verdict is the state it was reached
        // against. That marks the verdict provisional, so the thread is judged again once it resolves.
        var harness = new Harness();
        harness.WithJudgement(substantive: true, actedOn: false, inScope: true);

        await harness.Harvester.HandleThreadObservedAsync(HumanThread(status: "active"));

        await harness.Classifier.Received(1).JudgeAsync(
            Arg.Is<HumanMissJudgementRequest>(request => !request.ThreadResolved),
            Arg.Any<CancellationToken>());
        await harness.Misses.Received(1).RecordMissAsync(
            Arg.Any<CodeInsightPullRequestKey>(),
            Arg.Is<CodeInsightMissRecord>(miss => !miss.JudgedThreadResolved && !miss.CountsAsMiss),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AThreadJudgedWhileOpen_IsJudgedAgainOnceItResolves()
    {
        // Without this the verdict reached while the thread was open stands forever, and a concern the team
        // later accepted never counts against recall. The crawl observes threads only while the pull request is
        // open, so first observation is always the provisional case.
        var harness = new Harness();
        harness.WithStoredJudgement(threadResolved: false);
        harness.WithJudgement(substantive: true, actedOn: true, inScope: true);

        await harness.Harvester.HandleThreadObservedAsync(HumanThread(status: "fixed"));

        await harness.Classifier.Received(1).JudgeAsync(
            Arg.Is<HumanMissJudgementRequest>(request => request.ThreadResolved),
            Arg.Any<CancellationToken>());
        await harness.Misses.Received(1).RejudgeMissAsync(
            Arg.Any<CodeInsightPullRequestKey>(),
            Arg.Is<CodeInsightMissRecord>(miss => miss.JudgedThreadResolved && miss.CountsAsMiss),
            Arg.Any<CancellationToken>());
        await harness.Misses.DidNotReceive().RecordMissAsync(
            Arg.Any<CodeInsightPullRequestKey>(),
            Arg.Any<CodeInsightMissRecord>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Fixed")]
    [InlineData("Closed")]
    [InlineData("WontFix")]
    [InlineData("ByDesign")]
    public async Task AThreadJudgedWhileOpen_IsJudgedAgainOnEveryTerminalStatus(string status)
    {
        // Every status the provider can settle a thread at counts as resolved, not only "Fixed". WontFix and
        // ByDesign are a human accepting the concern outright, which is the strongest evidence the acted-on
        // judgement can have, and treating them as still open would strand exactly those threads on a
        // provisional verdict.
        var harness = new Harness();
        harness.WithStoredJudgement(threadResolved: false);

        await harness.Harvester.HandleThreadObservedAsync(HumanThread(status: status));

        await harness.Classifier.Received(1).JudgeAsync(
            Arg.Is<HumanMissJudgementRequest>(request => request.ThreadResolved),
            Arg.Any<CancellationToken>());
        await harness.Misses.Received(1).RejudgeMissAsync(
            Arg.Any<CodeInsightPullRequestKey>(),
            Arg.Is<CodeInsightMissRecord>(miss => miss.JudgedThreadResolved),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Active")]
    [InlineData("Pending")]
    [InlineData("Unknown")]
    [InlineData(null)]
    public async Task AThreadThatHasNotSettled_IsNotTreatedAsResolved(string? status)
    {
        var harness = new Harness();
        harness.WithStoredJudgement(threadResolved: false);

        await harness.Harvester.HandleThreadObservedAsync(HumanThread(status: status!));

        await harness.Classifier.DidNotReceive()
            .JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AThreadJudgedWhileOpen_IsNotJudgedAgainWhileItStaysOpen()
    {
        // The crawl re-observes it on every pass, and re-judging an unchanged open thread would spend a model
        // call per pass to reach the same answer.
        var harness = new Harness();
        harness.WithStoredJudgement(threadResolved: false);

        await harness.Harvester.HandleThreadObservedAsync(HumanThread(status: "active"));

        await harness.Classifier.DidNotReceive()
            .JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>());
        await harness.Misses.DidNotReceive().RejudgeMissAsync(
            Arg.Any<CodeInsightPullRequestKey>(),
            Arg.Any<CodeInsightMissRecord>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnUnjudgeableThread_RetainsFailureCoverageWithoutInventingAMiss()
    {
        // A failed judgement must remain visible so an incomplete harvest cannot appear to have zero misses.
        var harness = new Harness();
        harness.Classifier
            .JudgeAsync(Arg.Any<HumanMissJudgementRequest>(), Arg.Any<CancellationToken>())
            .Returns((HumanMissJudgement?)null);

        await harness.Harvester.HandleThreadObservedAsync(HumanThread());

        await harness.Misses.Received(1).RecordMissAsync(
            Arg.Any<CodeInsightPullRequestKey>(),
            Arg.Is<CodeInsightMissRecord>(record => record.JudgementFailed && !record.CountsAsMiss
                                                                           && !record.IsSubstantive && !record.IsInScope && !record.WasActedOn &&
                                                                           record.Confidence == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithTheGateClosed_NothingIsReadJudgedOrRecorded()
    {
        var harness = new Harness();
        harness.Gate.IsCollectionEnabledAsync(ClientId, Arg.Any<CancellationToken>()).Returns(false);

        await harness.Harvester.HandleThreadObservedAsync(HumanThread());

        await harness.Misses.DidNotReceive().GetJudgedThreadResolvedAsync(
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

        var exception = await Record.ExceptionAsync(() =>
            harness.Harvester.HandleThreadObservedAsync(HumanThread()));
        Assert.Null(exception);
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
            this.Misses.ObserveThreadEligibilityAsync(
                    Arg.Any<CodeInsightPullRequestKey>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<DateTimeOffset>(),
                    Arg.Any<CancellationToken>())
                .Returns(call => new CodeInsightThreadEligibilityObservation((DateTimeOffset)call[4], (bool)call[3]));
            this.Misses.ObserveUnchangedMissAsync(
                    Arg.Any<CodeInsightPullRequestKey>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string?>())
                .Returns(new CodeInsightMissAcknowledgment(false, false));
            this.Classifier = Substitute.For<IHumanMissClassifier>();
            this.Gate = Substitute.For<ICodeInsightsCollectionGate>();

            this.Gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
            this.Classifier.ClassifierVersion.Returns("test-miss");
            this.WithJudgement(true, true, true);
            this.WithFindings();

            // Null means the thread has not been harvested at all, which is the default starting point.
            this.Misses
                .GetJudgedThreadResolvedAsync(
                    Arg.Any<CodeInsightPullRequestKey>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>(),
                    Arg.Any<Guid?>(), Arg.Any<string?>())
                .Returns((bool?)null);
            this.Misses
                .RecordMissAsync(
                    Arg.Any<CodeInsightPullRequestKey>(),
                    Arg.Any<CodeInsightMissRecord>(),
                    Arg.Any<CancellationToken>())
                .Returns(true);
            this.Misses
                .RejudgeMissAsync(
                    Arg.Any<CodeInsightPullRequestKey>(),
                    Arg.Any<CodeInsightMissRecord>(),
                    Arg.Any<CancellationToken>())
                .Returns(true);

            this.Harvester = new CodeInsightMissHarvester(
                MeisterDev.ProPR.TestSupport.LocalScmPolicies.Registry.CompatibilityCodec,
                this.Store,
                this.Misses,
                this.Classifier,
                this.Gate,
                TestPostedCommentComposer.Default,
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

        /// <summary>Stands the thread up as already harvested, judged against the given resolved state.</summary>
        public void WithStoredJudgement(bool threadResolved)
        {
            this.Misses
                .GetJudgedThreadResolvedAsync(
                    Arg.Any<CodeInsightPullRequestKey>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>(),
                    Arg.Any<Guid?>(), Arg.Any<string?>())
                .Returns(threadResolved);
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

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Events;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.CodeInsights.Dispositions;

namespace MeisterDev.ProPR.CodeInsights.Tests.Dispositions;

public sealed class CodeInsightDispositionServiceTests
{
    private static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FindingId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task AClaimedFixWithACorroboratingChange_IsAddressedWithNoModelCall()
    {
        var harness = new Harness();

        await harness.Service.HandleThreadResolvedAsync(Resolved(ThreadResolutionIntent.ClaimsFix, ThreadAnchorCodeChange.Changed));

        await harness.Dispositions.Received(1).RecordDispositionAsync(
            FindingId,
            Arg.Is<CodeInsightDispositionRecord>(record =>
                record.Disposition == CodeInsightDisposition.Addressed
                && record.SourceIntent == ThreadResolutionIntent.ClaimsFix
                && record.SourceCodeChange == ThreadAnchorCodeChange.Changed
                && record.ClassifierVersion == null),
            Arg.Any<CancellationToken>());

        // The signals settle it, so no token is spent.
        await harness.Classifier.DidNotReceive()
            .JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AHumanAcceptance_IsAcknowledgedWithNoModelCall()
    {
        var harness = new Harness();

        await harness.Service.HandleThreadResolvedAsync(Resolved(ThreadResolutionIntent.AcceptedByHuman, ThreadAnchorCodeChange.Unchanged));

        await harness.Dispositions.Received(1).RecordDispositionAsync(
            FindingId,
            Arg.Is<CodeInsightDispositionRecord>(record =>
                record.Disposition == CodeInsightDisposition.Acknowledged && record.ClassifierVersion == null),
            Arg.Any<CancellationToken>());
        await harness.Classifier.DidNotReceive()
            .JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ADisregardedFindingJudgedWrong_IsAFalsePositiveAndRecordsTheClassifier()
    {
        var harness = new Harness();
        harness.WithJudgement(wasWrong: true, confidence: 0.9);

        await harness.Service.HandleThreadResolvedAsync(Resolved(ThreadResolutionIntent.ClaimsFix, ThreadAnchorCodeChange.Unchanged));

        await harness.Dispositions.Received(1).RecordDispositionAsync(
            FindingId,
            Arg.Is<CodeInsightDispositionRecord>(record =>
                record.Disposition == CodeInsightDisposition.FalsePositive
                && record.ClassifierVersion == "test-split"
                && record.ClassifierConfidence == 0.9),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ADisregardedFindingJudgedCorrectButUnwanted_IsDismissed()
    {
        var harness = new Harness();
        harness.WithJudgement(wasWrong: false, confidence: 0.7);

        await harness.Service.HandleThreadResolvedAsync(Resolved(ThreadResolutionIntent.Active, ThreadAnchorCodeChange.Unknown));

        await harness.Dispositions.Received(1).RecordDispositionAsync(
            FindingId,
            Arg.Is<CodeInsightDispositionRecord>(record =>
                record.Disposition == CodeInsightDisposition.Dismissed
                && record.ClassifierVersion == "test-split"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARejectionCarriesTheReasonItWasRejectedFor()
    {
        // The outcome says the finding was turned down. The reason is what says whether to fix the prompt, teach
        // the reviewer this codebase's conventions, or tell it what another tool already covers.
        var harness = new Harness();
        harness.WithJudgement(wasWrong: false, confidence: 0.7, CodeInsightRejectionReason.DesignTradeOff);

        await harness.Service.HandleThreadResolvedAsync(Resolved(ThreadResolutionIntent.Active, ThreadAnchorCodeChange.Unknown));

        await harness.Dispositions.Received(1).RecordDispositionAsync(
            FindingId,
            Arg.Is<CodeInsightDispositionRecord>(record =>
                record.Disposition == CodeInsightDisposition.Dismissed
                && record.RejectionReason == CodeInsightRejectionReason.DesignTradeOff),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AJudgedRejectionWithNoJudgedReason_IsStillRecorded()
    {
        // The reason is the only thing lost. Dropping the outcome with it would discard a judgement that was
        // made, and a rejection with no reason is reported as unclassified rather than counted as a reason.
        var harness = new Harness();
        harness.WithJudgement(wasWrong: true, confidence: 0.8);

        await harness.Service.HandleThreadResolvedAsync(Resolved(ThreadResolutionIntent.Active, ThreadAnchorCodeChange.Unknown));

        await harness.Dispositions.Received(1).RecordDispositionAsync(
            FindingId,
            Arg.Is<CodeInsightDispositionRecord>(record =>
                record.Disposition == CodeInsightDisposition.FalsePositive
                && record.RejectionReason == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AThreadThatEndedWithoutAVerdict_IsRecordedAsDiscussedRatherThanRejected()
    {
        // A human engaged and nobody decided. Before this outcome existed the classifier had to force such a
        // thread into a rejection, which charged the reviewer for a verdict nobody gave.
        var harness = new Harness();
        harness.Classifier
            .JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>())
            .Returns(new DisregardedFindingJudgement(false, 0.6, "argued and moved on", IsUnresolved: true));

        await harness.Service.HandleThreadResolvedAsync(Resolved(ThreadResolutionIntent.Active, ThreadAnchorCodeChange.Unchanged));

        await harness.Dispositions.Received(1).RecordDispositionAsync(
            FindingId,
            Arg.Is<CodeInsightDispositionRecord>(record =>
                record.Disposition == CodeInsightDisposition.Discussed
                // Nothing was rejected, so there is no rejection reason to carry.
                && record.RejectionReason == null
                && record.ClassifierConfidence == 0.6),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnOutcomeFromTheSignalsAlone_CarriesNoReason()
    {
        // No classifier ran, so there is nothing to say why, and a fixed finding was not rejected at all.
        var harness = new Harness();

        await harness.Service.HandleThreadResolvedAsync(Resolved(ThreadResolutionIntent.ClaimsFix, ThreadAnchorCodeChange.Changed));

        await harness.Dispositions.Received(1).RecordDispositionAsync(
            FindingId,
            Arg.Is<CodeInsightDispositionRecord>(record =>
                record.Disposition == CodeInsightDisposition.Addressed
                && record.RejectionReason == null
                && record.ClassifierVersion == null),
            Arg.Any<CancellationToken>());
        await harness.Classifier.DidNotReceiveWithAnyArgs()
            .JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnUnjudgeableSplit_FallsToDismissedRatherThanFalsePositive()
    {
        // Calling a finding wrong on the strength of a failed model call would charge the reviewer for a
        // mistake nobody established, and precision is the number that reads worst when inflated.
        var harness = new Harness();
        harness.Classifier
            .JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>())
            .Returns((DisregardedFindingJudgement?)null);

        await harness.Service.HandleThreadResolvedAsync(Resolved(ThreadResolutionIntent.ClaimsFix, ThreadAnchorCodeChange.Unchanged));

        await harness.Dispositions.Received(1).RecordDispositionAsync(
            FindingId,
            Arg.Is<CodeInsightDispositionRecord>(record =>
                record.Disposition == CodeInsightDisposition.Dismissed
                && record.ClassifierConfidence == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AThreadThatMatchesNoCollectedFinding_IsSkippedWithoutInventingARecord()
    {
        // Raised before collection was enabled, authored by a human, or on a provider whose thread ids were
        // never captured. Never attached to a finding that does not exist.
        var harness = new Harness();
        harness.Store
            .FindByProviderThreadAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<long>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns((CodeInsightFindingView?)null);

        await harness.Service.HandleThreadResolvedAsync(Resolved(ThreadResolutionIntent.ClaimsFix, ThreadAnchorCodeChange.Changed));

        await harness.Dispositions.DidNotReceive().RecordDispositionAsync(
            Arg.Any<Guid>(),
            Arg.Any<CodeInsightDispositionRecord>(),
            Arg.Any<CancellationToken>());
        await harness.Classifier.DidNotReceive()
            .JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnAlreadyDecidedFinding_IsLeftAloneAndCostsNoModelCall()
    {
        // A crawl observes the same resolved thread on every pass; re-deciding could change a number a report
        // has already shown.
        var harness = new Harness();
        harness.Dispositions
            .GetDispositionAsync(FindingId, Arg.Any<CancellationToken>())
            .Returns(
                new CodeInsightDispositionRecord(
                    CodeInsightDisposition.Addressed,
                    ThreadResolutionIntent.ClaimsFix,
                    ThreadAnchorCodeChange.Changed,
                    null,
                    null));

        await harness.Service.HandleThreadResolvedAsync(Resolved(ThreadResolutionIntent.ClaimsFix, ThreadAnchorCodeChange.Unchanged));

        await harness.Dispositions.DidNotReceive().RecordDispositionAsync(
            Arg.Any<Guid>(),
            Arg.Any<CodeInsightDispositionRecord>(),
            Arg.Any<CancellationToken>());
        await harness.Classifier.DidNotReceive()
            .JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithTheGateClosed_NothingIsLookedUpRecordedOrJudged()
    {
        var harness = new Harness();
        harness.Gate.IsCollectionEnabledAsync(ClientId, Arg.Any<CancellationToken>()).Returns(false);

        await harness.Service.HandleThreadResolvedAsync(Resolved(ThreadResolutionIntent.ClaimsFix, ThreadAnchorCodeChange.Changed));

        await harness.Store.DidNotReceive().FindByProviderThreadAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<long>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await harness.Dispositions.DidNotReceive().RecordDispositionAsync(
            Arg.Any<Guid>(),
            Arg.Any<CodeInsightDispositionRecord>(),
            Arg.Any<CancellationToken>());
        await harness.Classifier.DidNotReceive()
            .JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheThreadIdIsJoinedInItsInvariantStringForm()
    {
        // The crawl carries a number, the store holds the provider's own string. A culture-dependent
        // conversion would silently never match and the whole feature would record nothing.
        var harness = new Harness();

        await harness.Service.HandleThreadResolvedAsync(Resolved(ThreadResolutionIntent.ClaimsFix, ThreadAnchorCodeChange.Changed));

        await harness.Store.Received(1).FindByProviderThreadAsync(
            ClientId,
            "repo-1",
            7,
            "9001",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheJudgementSeesTheFindingTextAndTheDiscussionThatClosedIt()
    {
        var harness = new Harness();
        harness.WithJudgement(wasWrong: false, confidence: 0.5);

        await harness.Service.HandleThreadResolvedAsync(Resolved(ThreadResolutionIntent.Active, ThreadAnchorCodeChange.Unknown));

        await harness.Classifier.Received(1).JudgeAsync(
            Arg.Is<DisregardedFindingJudgementRequest>(request =>
                request.ClientId == ClientId
                && request.FindingId == FindingId
                && request.FindingMessage == "The null check is missing."
                && request.CommentHistory == "dev: not now, tracked elsewhere"
                && request.FilePath == "src/Service.cs"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFailureAnywhereIsSwallowedSoTheCrawlIsUnaffected()
    {
        var harness = new Harness();
        harness.Store
            .FindByProviderThreadAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<long>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the database is unreachable"));

        // No exception reaches the caller: this is a side-write on the crawl's thread.
        var exception = await Record.ExceptionAsync(() =>
            harness.Service.HandleThreadResolvedAsync(Resolved(ThreadResolutionIntent.ClaimsFix, ThreadAnchorCodeChange.Changed)));
        Assert.Null(exception);
    }

    private static ThreadResolvedDomainEvent Resolved(
        ThreadResolutionIntent intent,
        ThreadAnchorCodeChange codeChange)
    {
        return new ThreadResolvedDomainEvent(
            ClientId,
            "repo-1",
            7,
            9001,
            "src/Service.cs",
            "@@ -1 +1 @@",
            "dev: not now, tracked elsewhere",
            DateTimeOffset.UtcNow,
            intent,
            codeChange);
    }

    private sealed class Harness
    {
        public Harness()
        {
            this.Store = Substitute.For<ICodeInsightFindingStore>();
            this.Dispositions = Substitute.For<ICodeInsightDispositionStore>();
            this.Classifier = Substitute.For<IDisregardedFindingClassifier>();
            this.Gate = Substitute.For<ICodeInsightsCollectionGate>();

            this.Gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
            this.Classifier.ClassifierVersion.Returns("test-split");
            this.Store
                .FindByProviderThreadAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<string>(),
                    Arg.Any<long>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>())
                .Returns(
                    new CodeInsightFindingView(
                        FindingId,
                        Guid.NewGuid(),
                        "rev-1",
                        0,
                        "src/Service.cs",
                        42,
                        CommentSeverity.Error,
                        "The null check is missing.",
                        "9001",
                        DateTimeOffset.UtcNow));
            this.Dispositions
                .GetDispositionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns((CodeInsightDispositionRecord?)null);
            this.Dispositions
                .RecordDispositionAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<CodeInsightDispositionRecord>(),
                    Arg.Any<CancellationToken>())
                .Returns(true);

            this.Service = new CodeInsightDispositionService(
                this.Store,
                this.Dispositions,
                this.Classifier,
                this.Gate,
                NullLogger<CodeInsightDispositionService>.Instance);
        }

        public ICodeInsightFindingStore Store { get; }

        public ICodeInsightDispositionStore Dispositions { get; }

        public IDisregardedFindingClassifier Classifier { get; }

        public ICodeInsightsCollectionGate Gate { get; }

        public CodeInsightDispositionService Service { get; }

        public void WithJudgement(
            bool wasWrong,
            double confidence,
            CodeInsightRejectionReason? reason = null)
        {
            this.Classifier
                .JudgeAsync(Arg.Any<DisregardedFindingJudgementRequest>(), Arg.Any<CancellationToken>())
                .Returns(new DisregardedFindingJudgement(wasWrong, confidence, "because", reason));
        }
    }
}

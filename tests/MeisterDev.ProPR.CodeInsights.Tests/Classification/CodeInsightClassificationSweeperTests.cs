// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.CodeInsights.Taxonomy;
using MeisterDev.ProPR.CodeInsights.Classification;

namespace MeisterDev.ProPR.CodeInsights.Tests.Classification;

public sealed class CodeInsightClassificationSweeperTests
{
    private static readonly Guid ClientA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ClientB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task SweepOnceAsync_AnEmptyBacklogCostsNothing()
    {
        var harness = new Harness();
        harness.WithBacklog();

        var result = await harness.Sweeper.SweepOnceAsync();

        Assert.Equal(0, result.Considered);
        await harness.Classifier.DidNotReceive()
            .ClassifyAsync(Arg.Any<FindingClassificationRequest>(), Arg.Any<CancellationToken>());
        await harness.Gate.DidNotReceive()
            .IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SweepOnceAsync_ClassifiesEachFindingAndStampsTheClassifierVersion()
    {
        var harness = new Harness();
        var first = NewFinding(ClientA);
        var second = NewFinding(ClientA);
        harness.WithBacklog(first, second);

        var result = await harness.Sweeper.SweepOnceAsync();

        Assert.Equal(2, result.Considered);
        Assert.Equal(2, result.Classified);
        Assert.Equal(0, result.Failed);

        await harness.Store.Received(1).ApplyClassificationAsync(
            first.Id,
            Arg.Is<CodeInsightClassification>(classification =>
                classification.CoreSlugs.Contains(CodeInsightCoreTaxonomy.LogicError)
                && classification.Level == CodeInsightFindingLevel.Member
                && classification.Qualifier == CodeInsightFindingQualifier.Missing
                && classification.ClassifierVersion == "test-classifier"),
            Arg.Any<CancellationToken>());
        await harness.Store.Received(1).ApplyClassificationAsync(
            second.Id,
            Arg.Any<CodeInsightClassification>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SweepOnceAsync_WithTheGateClosed_LeavesTheFindingUntouchedAndSpendsNoAttempt()
    {
        // Deliberately no attempt: a finding must not burn through its retries while its client is gated
        // off, or opting back in would leave it permanently unclassifiable.
        var harness = new Harness();
        harness.WithGate(ClientA, enabled: false);
        harness.WithBacklog(NewFinding(ClientA));

        var result = await harness.Sweeper.SweepOnceAsync();

        Assert.Equal(1, result.SkippedByGate);
        Assert.Equal(0, result.Classified);
        await harness.Classifier.DidNotReceive()
            .ClassifyAsync(Arg.Any<FindingClassificationRequest>(), Arg.Any<CancellationToken>());
        await harness.Store.DidNotReceive()
            .RecordClassificationAttemptAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await harness.Store.DidNotReceive().ApplyClassificationAsync(
            Arg.Any<Guid>(),
            Arg.Any<CodeInsightClassification>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SweepOnceAsync_WithNoModelBoundForThePurpose_SpendsNoAttempt()
    {
        // The failure this prevents: an installation switches Code Insights on before binding a model, every
        // finding burns its three attempts against a purpose nothing was ever asked, and binding a model
        // afterwards changes nothing because the findings have already been written off as unclassifiable.
        var harness = new Harness();
        harness.Classifier
            .ClassifyAsync(Arg.Any<FindingClassificationRequest>(), Arg.Any<CancellationToken>())
            .Returns(FindingClassificationResult.NoModelBound());
        var finding = NewFinding(ClientA);
        harness.WithBacklog(finding);

        var result = await harness.Sweeper.SweepOnceAsync();

        Assert.Equal(0, result.Classified);
        // Not counted as a failure either: nothing failed, there was nothing to ask.
        Assert.Equal(0, result.Failed);
        await harness.Store.DidNotReceive()
            .RecordClassificationAttemptAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await harness.Store.DidNotReceive().ApplyClassificationAsync(
            Arg.Any<Guid>(),
            Arg.Any<CodeInsightClassification>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SweepOnceAsync_OneClientBeingGatedOffDoesNotStopAnother()
    {
        var harness = new Harness();
        harness.WithGate(ClientA, enabled: false);
        harness.WithGate(ClientB, enabled: true);
        var gated = NewFinding(ClientA);
        var allowed = NewFinding(ClientB);
        harness.WithBacklog(gated, allowed);

        var result = await harness.Sweeper.SweepOnceAsync();

        Assert.Equal(1, result.SkippedByGate);
        Assert.Equal(1, result.Classified);
        await harness.Store.Received(1).ApplyClassificationAsync(
            allowed.Id,
            Arg.Any<CodeInsightClassification>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SweepOnceAsync_AsksTheGateAndTheVocabularyOncePerClientNotOncePerFinding()
    {
        var harness = new Harness();
        harness.WithBacklog(NewFinding(ClientA), NewFinding(ClientA), NewFinding(ClientA));

        await harness.Sweeper.SweepOnceAsync();

        await harness.Gate.Received(1).IsCollectionEnabledAsync(ClientA, Arg.Any<CancellationToken>());
        await harness.TaxonomyService.Received(1)
            .GetAssignableTaxonomyAsync(ClientA, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SweepOnceAsync_ClassifiesAgainstTheAssignableVocabularyOnly()
    {
        // Retired custom tags must not be handed to the classifier: it would assign a tag that is no longer
        // offered, which is exactly what retiring one is meant to stop.
        var harness = new Harness();
        harness.WithBacklog(NewFinding(ClientA));

        await harness.Sweeper.SweepOnceAsync();

        await harness.TaxonomyService.Received(1)
            .GetAssignableTaxonomyAsync(ClientA, Arg.Any<CancellationToken>());
        await harness.TaxonomyService.DidNotReceive()
            .GetTaxonomyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SweepOnceAsync_AnUnclassifiableFindingSpendsAnAttemptSoItIsNotRetriedForever()
    {
        var harness = new Harness();
        harness.Classifier
            .ClassifyAsync(Arg.Any<FindingClassificationRequest>(), Arg.Any<CancellationToken>())
            .Returns(FindingClassificationResult.Unusable());
        var finding = NewFinding(ClientA);
        harness.WithBacklog(finding);

        var result = await harness.Sweeper.SweepOnceAsync();

        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.Classified);
        await harness.Store.Received(1)
            .RecordClassificationAttemptAsync(finding.Id, Arg.Any<CancellationToken>());
        await harness.Store.DidNotReceive().ApplyClassificationAsync(
            Arg.Any<Guid>(),
            Arg.Any<CodeInsightClassification>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SweepOnceAsync_AThrowingClassifierDoesNotAbortTheRestOfTheBatch()
    {
        var harness = new Harness();
        var doomed = NewFinding(ClientA);
        var healthy = NewFinding(ClientA);
        harness.Classifier
            .ClassifyAsync(
                Arg.Is<FindingClassificationRequest>(request => request.FindingId == doomed.Id),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the classifier exploded"));
        harness.WithBacklog(doomed, healthy);

        var result = await harness.Sweeper.SweepOnceAsync();

        Assert.Equal(1, result.Failed);
        Assert.Equal(1, result.Classified);
        await harness.Store.Received(1)
            .RecordClassificationAttemptAsync(doomed.Id, Arg.Any<CancellationToken>());
        await harness.Store.Received(1).ApplyClassificationAsync(
            healthy.Id,
            Arg.Any<CodeInsightClassification>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SweepOnceAsync_RefreshesEachAffectedJobsRollupOnceNotOncePerFinding()
    {
        // The projector recomputes a whole job's cells, so calling it per finding would repeat the same work
        // for every finding of that job.
        var harness = new Harness();
        var jobA = Guid.NewGuid();
        var jobB = Guid.NewGuid();
        harness.WithBacklog(
            NewFinding(ClientA, jobA),
            NewFinding(ClientA, jobA),
            NewFinding(ClientA, jobB));

        await harness.Sweeper.SweepOnceAsync();

        await harness.RollupProjector.Received(1).ProjectJobAsync(jobA, Arg.Any<CancellationToken>());
        await harness.RollupProjector.Received(1).ProjectJobAsync(jobB, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SweepOnceAsync_WithNothingClassified_DoesNotTouchTheRollup()
    {
        var harness = new Harness();
        harness.Classifier
            .ClassifyAsync(Arg.Any<FindingClassificationRequest>(), Arg.Any<CancellationToken>())
            .Returns(FindingClassificationResult.Unusable());
        harness.WithBacklog(NewFinding(ClientA));

        await harness.Sweeper.SweepOnceAsync();

        await harness.RollupProjector.DidNotReceive()
            .ProjectJobAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SweepOnceAsync_RequestsABoundedBatchUnderTheAttemptCeiling()
    {
        var harness = new Harness();
        harness.WithBacklog(NewFinding(ClientA));

        await harness.Sweeper.SweepOnceAsync();

        await harness.Store.Received(1).ListUnclassifiedAsync(
            CodeInsightClassificationSweeper.DefaultBatchSize,
            CodeInsightClassificationSweeper.DefaultMaxAttempts,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SweepOnceAsync_ReportsTheRemainingBacklogSoGrowthIsVisible()
    {
        var harness = new Harness();
        harness.WithBacklog(NewFinding(ClientA));
        harness.Store
            .CountUnclassifiedAsync(
                CodeInsightClassificationSweeper.DefaultMaxAttempts,
                Arg.Any<CancellationToken>())
            .Returns(4_242);

        var result = await harness.Sweeper.SweepOnceAsync();

        Assert.Equal(4_242, result.BacklogRemaining);
    }

    [Fact]
    public async Task SweepOnceAsync_NeverRunsMoreClassificationsAtOnceThanTheConcurrencyCap()
    {
        // The review path shares the client's model quota; starving it to classify analytics would be the
        // wrong trade, so the cap is a property worth asserting rather than trusting.
        var harness = new Harness();
        var inFlight = 0;
        var peak = 0;
        var gate = new TaskCompletionSource();

        harness.Classifier
            .ClassifyAsync(Arg.Any<FindingClassificationRequest>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                var current = Interlocked.Increment(ref inFlight);
                InterlockedMax(ref peak, current);
                await gate.Task;
                Interlocked.Decrement(ref inFlight);
                return FindingClassificationResult.Classified(Verdict());
            });

        var findings = Enumerable.Range(0, 20).Select(_ => NewFinding(ClientA)).ToArray();
        harness.WithBacklog(findings);

        var sweep = harness.Sweeper.SweepOnceAsync();

        // Let the queued calls settle against the semaphore before releasing them.
        while (Volatile.Read(ref inFlight) < CodeInsightClassificationSweeper.DefaultMaxConcurrency)
        {
            await Task.Yield();
        }

        await Task.Delay(50);
        gate.SetResult();
        await sweep;

        Assert.True(
            peak <= CodeInsightClassificationSweeper.DefaultMaxConcurrency,
            $"Peak concurrency was {peak}, cap is {CodeInsightClassificationSweeper.DefaultMaxConcurrency}.");
    }

    private static void InterlockedMax(ref int target, int candidate)
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref target);
            if (candidate <= seen)
            {
                return;
            }
        } while (Interlocked.CompareExchange(ref target, candidate, seen) != seen);
    }

    private static CodeInsightUnclassifiedFinding NewFinding(Guid clientId, Guid? jobId = null)
    {
        return new CodeInsightUnclassifiedFinding(
            Guid.CreateVersion7(),
            clientId,
            jobId ?? Guid.NewGuid(),
            "The null check is missing.",
            "src/Service.cs",
            42,
            CommentSeverity.Error,
            "Baseline",
            0);
    }

    private static FindingTypeVerdict Verdict()
    {
        return new FindingTypeVerdict(
            [CodeInsightCoreTaxonomy.LogicError],
            [],
            CodeInsightFindingLevel.Member,
            CodeInsightFindingQualifier.Missing,
            0.8);
    }

    private sealed class Harness
    {
        public Harness()
        {
            this.Store = Substitute.For<ICodeInsightClassificationStore>();
            this.Classifier = Substitute.For<IFindingTypeClassifier>();
            this.TaxonomyService = Substitute.For<ICodeInsightTaxonomyService>();
            this.Gate = Substitute.For<ICodeInsightsCollectionGate>();
            this.RollupProjector = Substitute.For<ICodeInsightRollupProjector>();

            this.Classifier.ClassifierVersion.Returns("test-classifier");
            this.Classifier
                .ClassifyAsync(Arg.Any<FindingClassificationRequest>(), Arg.Any<CancellationToken>())
                .Returns(FindingClassificationResult.Classified(Verdict()));
            this.TaxonomyService
                .GetAssignableTaxonomyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(new CodeInsightTaxonomyDto(CodeInsightCoreTaxonomy.Version, [], []));
            this.Gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);

            this.Sweeper = new CodeInsightClassificationSweeper(
                this.Store,
                this.Classifier,
                this.TaxonomyService,
                this.Gate,
                NullLogger<CodeInsightClassificationSweeper>.Instance,
                this.RollupProjector);
        }

        public ICodeInsightClassificationStore Store { get; }

        public IFindingTypeClassifier Classifier { get; }

        public ICodeInsightTaxonomyService TaxonomyService { get; }

        public ICodeInsightsCollectionGate Gate { get; }

        public ICodeInsightRollupProjector RollupProjector { get; }

        public CodeInsightClassificationSweeper Sweeper { get; }

        public void WithBacklog(params CodeInsightUnclassifiedFinding[] findings)
        {
            this.Store
                .ListUnclassifiedAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(findings);
        }

        public void WithGate(Guid clientId, bool enabled)
        {
            this.Gate.IsCollectionEnabledAsync(clientId, Arg.Any<CancellationToken>()).Returns(enabled);
        }
    }
}

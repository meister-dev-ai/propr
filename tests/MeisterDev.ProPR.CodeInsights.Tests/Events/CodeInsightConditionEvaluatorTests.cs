// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Events;
using MeisterDev.ProPR.CodeInsights.Metrics;
using MeisterDev.ProPR.CodeInsights.Rollups;

namespace MeisterDev.ProPR.CodeInsights.Tests.Events;

/// <summary>
///     The quality-condition event log. What matters here is fire-once: a condition that stays true for a month
///     must be one row, or the table is useless as an alert source.
/// </summary>
public sealed class CodeInsightConditionEvaluatorTests : IDisposable
{
    private static readonly Guid ClientA = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly DateOnly AsOf = new(2026, 6, 30);

    private static readonly CodeInsightConditionThresholds Thresholds = new(
        WindowDays: 28,
        CorrectnessDeclineThreshold: 0.10,
        FalsePositiveShareThreshold: 0.30,
        ConcentrationThreshold: 25,
        MinimumSealedPullRequests: 10);

    private readonly MeisterProPRDbContext _dbContext;
    private readonly CodeInsightEventStore _store;
    private readonly ICodeInsightMetricReader _metrics;
    private readonly ICodeInsightRollupReader _rollups;
    private readonly ICodeInsightsCollectionGate _gate;
    private readonly CodeInsightConditionEvaluator _evaluator;

    public CodeInsightConditionEvaluatorTests()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"CodeInsightConditionTests-{Guid.NewGuid():N}")
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);
        this._store = new CodeInsightEventStore(this._dbContext);

        this._metrics = Substitute.For<ICodeInsightMetricReader>();
        this._rollups = Substitute.For<ICodeInsightRollupReader>();
        this._gate = Substitute.For<ICodeInsightsCollectionGate>();
        this._gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);

        this.WithCorrectness();
        this.WithAcceptance(falsePositive: 0, resolved: 0);
        this.WithHotspots();

        this._evaluator = new CodeInsightConditionEvaluator(
            this._metrics,
            this._rollups,
            this._store,
            this._gate,
            NullLogger<CodeInsightConditionEvaluator>.Instance);
    }

    public void Dispose()
    {
        this._dbContext.Dispose();
    }

    [Fact]
    public async Task ADecliningCorrectnessTrendFiresWithTheContextAConditionNeeds()
    {
        this.WithCorrectness((new DateOnly(2026, 6, 1), 0.80, 12), (new DateOnly(2026, 6, 22), 0.55, 14));

        Assert.Equal(1, await this.EvaluateAsync());

        var evt = Assert.Single(await this.ReadEventsAsync(CodeInsightEventType.CorrectnessDeclining));
        Assert.Equal(CodeInsightConditionState.Firing, evt.State);
        Assert.Equal("f1", evt.Metric);
        Assert.Equal(CodeInsightEventDirection.Fell, evt.Direction);
        Assert.Equal(0.55, evt.ObservedValue, 12);
        Assert.Equal(0.80, evt.PreviousValue!.Value, 12);
        Assert.Equal(0.25, evt.Magnitude, 12);
        Assert.Equal(0.10, evt.ThresholdValue, 12);
        // The evidence behind it, so a consumer can ignore a thin signal.
        Assert.Equal(14, evt.SampleSize);
        Assert.Equal(AsOf, evt.WindowTo);
        Assert.Equal(AsOf.AddDays(-28), evt.WindowFrom);
        // Client-wide: every scope part is a complete key, with the empty string for "not applicable".
        Assert.Equal(string.Empty, evt.RepositoryId);
        Assert.Equal(string.Empty, evt.FilePath);
    }

    [Fact]
    public async Task AConditionThatStaysTrueFiresOnceRatherThanOnEveryEvaluation()
    {
        // The whole point of the state column. An event per evaluation makes the table useless as an alert source.
        this.WithCorrectness((new DateOnly(2026, 6, 1), 0.80, 12), (new DateOnly(2026, 6, 22), 0.55, 14));

        Assert.Equal(1, await this.EvaluateAsync());
        Assert.Equal(0, await this.EvaluateAsync());
        Assert.Equal(0, await this.EvaluateAsync());

        Assert.Single(await this.ReadEventsAsync(CodeInsightEventType.CorrectnessDeclining));
    }

    [Fact]
    public async Task AConditionThatStopsBeingTrueRecordsItsClearing()
    {
        // Which is the recovery signal any alerting integration needs, and what makes fire-once implementable
        // from this one table.
        this.WithCorrectness((new DateOnly(2026, 6, 1), 0.80, 12), (new DateOnly(2026, 6, 22), 0.55, 14));
        await this.EvaluateAsync();

        this.WithCorrectness((new DateOnly(2026, 6, 1), 0.80, 12), (new DateOnly(2026, 6, 22), 0.78, 14));
        Assert.Equal(1, await this.EvaluateAsync());

        var events = await this.ReadEventsAsync(CodeInsightEventType.CorrectnessDeclining);
        Assert.Equal(2, events.Count);
        Assert.Equal(CodeInsightConditionState.Firing, events[0].State);
        Assert.Equal(CodeInsightConditionState.Cleared, events[1].State);
    }

    [Fact]
    public async Task AConditionThatFiresAgainAfterClearingIsANewTransition()
    {
        this.WithCorrectness((new DateOnly(2026, 6, 1), 0.80, 12), (new DateOnly(2026, 6, 22), 0.55, 14));
        await this.EvaluateAsync();
        this.WithCorrectness((new DateOnly(2026, 6, 1), 0.80, 12), (new DateOnly(2026, 6, 22), 0.78, 14));
        await this.EvaluateAsync();
        this.WithCorrectness((new DateOnly(2026, 6, 1), 0.80, 12), (new DateOnly(2026, 6, 22), 0.50, 14));

        Assert.Equal(1, await this.EvaluateAsync());

        var events = await this.ReadEventsAsync(CodeInsightEventType.CorrectnessDeclining);
        Assert.Equal(3, events.Count);
        Assert.Equal(CodeInsightConditionState.Firing, events[^1].State);
    }

    [Fact]
    public async Task NoTransitionMeansNoEvent()
    {
        this.WithCorrectness((new DateOnly(2026, 6, 1), 0.80, 12), (new DateOnly(2026, 6, 22), 0.79, 14));
        this.WithAcceptance(falsePositive: 1, resolved: 20);
        this.WithHotspots(("repo-1", "quiet.cs", 3));

        Assert.Equal(0, await this.EvaluateAsync());
        Assert.Empty(await this.ReadEventsAsync());
    }

    [Fact]
    public async Task ACorrectnessDeclineOnTooThinASampleDoesNotFire()
    {
        // Otherwise the first two closed pull requests of a quiet week could raise an alert about the reviewer.
        this.WithCorrectness((new DateOnly(2026, 6, 1), 0.90, 2), (new DateOnly(2026, 6, 22), 0.20, 3));

        Assert.Equal(0, await this.EvaluateAsync());
        Assert.Empty(await this.ReadEventsAsync(CodeInsightEventType.CorrectnessDeclining));
    }

    [Fact]
    public async Task ASingleComparablePeriodIsNotADeclineAndNotARecoveryEither()
    {
        // Absence of evidence must not read as either.
        this.WithCorrectness((new DateOnly(2026, 6, 22), 0.20, 30));

        Assert.Equal(0, await this.EvaluateAsync());
        Assert.Empty(await this.ReadEventsAsync());
    }

    [Fact]
    public async Task AFalsePositiveShareAboveTheThresholdFiresWithItsOwnSample()
    {
        this.WithAcceptance(falsePositive: 9, resolved: 20);

        Assert.Equal(1, await this.EvaluateAsync());

        var evt = Assert.Single(await this.ReadEventsAsync(CodeInsightEventType.FalsePositiveShareHigh));
        Assert.Equal("false-positive-share", evt.Metric);
        Assert.Equal(CodeInsightEventDirection.Rose, evt.Direction);
        Assert.Equal(0.45, evt.ObservedValue, 12);
        // A level, not a change: there is nothing to compare it to.
        Assert.Null(evt.PreviousValue);
        Assert.Equal(20, evt.SampleSize);
    }

    [Fact]
    public async Task NothingResolvedIsNotAFalsePositiveProblem()
    {
        this.WithAcceptance(falsePositive: 0, resolved: 0);

        Assert.Equal(0, await this.EvaluateAsync());
        Assert.Empty(await this.ReadEventsAsync(CodeInsightEventType.FalsePositiveShareHigh));
    }

    [Fact]
    public async Task AHotspotFiresPerFileSoOneNoisyFileDoesNotMaskAnother()
    {
        this.WithHotspots(("repo-1", "hot.cs", 40), ("repo-1", "hotter.cs", 60), ("repo-1", "quiet.cs", 2));

        Assert.Equal(2, await this.EvaluateAsync());

        var events = await this.ReadEventsAsync(CodeInsightEventType.ConcentrationHotspot);
        Assert.Equal(2, events.Count);
        Assert.All(events, evt => Assert.Equal("repo-1", evt.RepositoryId));
        Assert.All(events, evt => Assert.Equal("finding-count", evt.Metric));
        Assert.Contains(events, evt => evt.FilePath == "hot.cs" && evt.ObservedValue == 40);
        Assert.Contains(events, evt => evt.FilePath == "hotter.cs" && evt.ObservedValue == 60);
    }

    [Fact]
    public async Task APullRequestLevelRowIsNeverAHotspot()
    {
        // "The empty path is a hotspot" would be a meaningless alert.
        this.WithHotspots(("repo-1", null, 90));

        Assert.Equal(0, await this.EvaluateAsync());
        Assert.Empty(await this.ReadEventsAsync(CodeInsightEventType.ConcentrationHotspot));
    }

    [Fact]
    public async Task NothingIsEmittedWhenTheGateIsClosed()
    {
        this.WithCorrectness((new DateOnly(2026, 6, 1), 0.80, 12), (new DateOnly(2026, 6, 22), 0.40, 14));
        this.WithAcceptance(falsePositive: 9, resolved: 20);
        this._gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);

        Assert.Equal(0, await this.EvaluateAsync());
        Assert.Empty(await this.ReadEventsAsync());
        // Nothing was even read: the gate is asked before any work is done.
        await this._metrics.DidNotReceive().GetCorrectnessSeriesAsync(
            Arg.Any<CodeInsightRollupQuery>(),
            Arg.Any<CodeInsightBucketSize>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFailingReadIsSwallowedSoMetricComputationIsNeverDisturbed()
    {
        this._metrics
            .GetCorrectnessSeriesAsync(
                Arg.Any<CodeInsightRollupQuery>(),
                Arg.Any<CodeInsightBucketSize>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the metrics store is unavailable"));

        Assert.Equal(0, await this.EvaluateAsync());
        Assert.Empty(await this.ReadEventsAsync());
    }

    [Fact]
    public async Task TheEvaluationIsScopedToTheClientItWasAskedAbout()
    {
        this.WithAcceptance(falsePositive: 9, resolved: 20);

        await this.EvaluateAsync();

        var query = this._metrics.ReceivedCalls()
            .Select(call => call.GetArguments()[0])
            .OfType<CodeInsightRollupQuery>()
            .First();
        Assert.Equal(ClientA, Assert.Single(query.ClientIds));
    }

    private Task<int> EvaluateAsync()
    {
        return this._evaluator.EvaluateAsync(ClientA, AsOf, Thresholds);
    }

    private async Task<IReadOnlyList<Domain.Entities.CodeInsightEvent>> ReadEventsAsync(CodeInsightEventType? eventType = null)
    {
        var events = await this._store.GetByClientSinceAsync(ClientA, DateTimeOffset.MinValue);
        return eventType is null
            ? events
            : events.Where(evt => evt.EventType == eventType.Value).ToList();
    }

    private void WithCorrectness(params (DateOnly Bucket, double F1, int SampleSize)[] points)
    {
        this._metrics
            .GetCorrectnessSeriesAsync(
                Arg.Any<CodeInsightRollupQuery>(),
                Arg.Any<CodeInsightBucketSize>(),
                Arg.Any<CancellationToken>())
            .Returns(
                points
                    .Select(point => new CodeInsightMetricSeriesPoint(
                        point.Bucket,
                        new CodeInsightMetricResult(
                            new CodeInsightMetrics(default, point.F1, point.F1, point.F1, point.F1),
                            point.SampleSize)))
                    .ToList());
    }

    private void WithAcceptance(int falsePositive, int resolved)
    {
        var addressed = Math.Max(resolved - falsePositive, 0);
        var inputs = new CodeInsightMetricInputs(addressed, 0, 0, falsePositive, 0);

        this._metrics
            .GetAcceptanceAsync(Arg.Any<CodeInsightRollupQuery>(), Arg.Any<CancellationToken>())
            .Returns(new CodeInsightMetricResult(CodeInsightMetricCalculator.Compute(inputs), inputs.Resolved));
    }

    private void WithHotspots(params (string Repository, string? FilePath, int Count)[] rows)
    {
        this._rollups
            .GetConcentrationAsync(
                Arg.Any<CodeInsightRollupQuery>(),
                Arg.Any<CodeInsightGrain>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(
                rows
                    .Select(row => new CodeInsightConcentrationRow(
                        ClientA,
                        row.Repository,
                        null,
                        row.FilePath,
                        null,
                        row.Count))
                    .ToList());
    }
}

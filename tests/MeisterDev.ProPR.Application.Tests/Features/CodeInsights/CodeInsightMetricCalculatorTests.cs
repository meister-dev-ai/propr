// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.CodeInsights.Metrics;

namespace MeisterDev.ProPR.Application.Tests.Features.CodeInsights;

/// <summary>
///     The arithmetic behind both headline metrics. Pure on purpose, because reproducing a metric from its
///     stored inputs is an acceptance criterion and that is only trustworthy if the computation depends on
///     nothing else.
/// </summary>
public sealed class CodeInsightMetricCalculatorTests
{
    [Fact]
    public void BothLensesAreComputedFromTheSameCounts()
    {
        // 6 addressed + 2 acknowledged + 1 dismissed = 9 true positives; 1 wrong; 2 missed.
        var metrics = CodeInsightMetricCalculator.Compute(
            new CodeInsightMetricInputs(Addressed: 6, Acknowledged: 2, Dismissed: 1, FalsePositive: 1, Misses: 2));

        Assert.Equal(9d / 10d, metrics.Precision);
        Assert.Equal(9d / 11d, metrics.Recall);
        Assert.Equal(2d * 9 / (2 * 9 + 1 + 2), metrics.F1!.Value, 12);
        // Accepted is addressed + acknowledged only, over the 10 that resolved.
        Assert.Equal(8d / 10d, metrics.AcceptanceRate);
    }

    [Fact]
    public void AnUnresolvedDiscussionMovesNeitherRatio()
    {
        // A thread that fizzled is not evidence the finding was right, nor evidence it was unwanted. Counting it
        // either way would put a verdict nobody gave into both lenses.
        var inputs = new CodeInsightMetricInputs(Addressed: 6, Acknowledged: 2, Dismissed: 1, FalsePositive: 1, Misses: 2);
        var withDiscussion = inputs with { Discussed = 5 };

        var before = CodeInsightMetricCalculator.Compute(inputs);
        var after = CodeInsightMetricCalculator.Compute(withDiscussion);

        Assert.Equal(before.Precision, after.Precision);
        Assert.Equal(before.Recall, after.Recall);
        Assert.Equal(before.F1, after.F1);
        Assert.Equal(before.AcceptanceRate, after.AcceptanceRate);
        // And it is in neither denominator, so the sample the acceptance rate is a proportion of does not grow.
        Assert.Equal(inputs.Resolved, withDiscussion.Resolved);
        Assert.Equal(5, withDiscussion.Discussed);
    }

    [Fact]
    public void DiscussedFindingsSurviveAggregation()
    {
        // The count has to roll up like every other, or a repository total would silently lose it.
        var summed = CodeInsightMetricInputs.Sum(
        [
            new CodeInsightMetricInputs(1, 0, 0, 0, 0, Discussed: 2),
            new CodeInsightMetricInputs(0, 1, 0, 0, 0, Discussed: 3),
        ]);

        Assert.Equal(5, summed.Discussed);
        Assert.Equal(2, summed.Resolved);
    }

    [Fact]
    public void DismissedIsATruePositiveButIsNotAccepted()
    {
        // The two lenses disagreeing about the same finding is the point of having both: it was a correct
        // finding that the team chose not to act on.
        var metrics = CodeInsightMetricCalculator.Compute(new CodeInsightMetricInputs(0, 0, Dismissed: 4, FalsePositive: 0, Misses: 0));

        Assert.Equal(1d, metrics.Precision);
        Assert.Equal(0d, metrics.AcceptanceRate);
    }

    [Fact]
    public void NothingResolvedAndNothingMissedLeavesEveryRatioUndefined()
    {
        // Undefined, not zero. A metric that reports 0 for "nothing happened" is a lie a chart draws as a
        // collapse in quality.
        var metrics = CodeInsightMetricCalculator.Compute(default);

        Assert.Null(metrics.Precision);
        Assert.Null(metrics.Recall);
        Assert.Null(metrics.F1);
        Assert.Null(metrics.AcceptanceRate);
    }

    [Fact]
    public void OnlyMissesGivesZeroRecallAndUndefinedPrecision()
    {
        // The reviewer raised nothing, so there is nothing to be right or wrong about, but it did miss things,
        // which recall can legitimately report as zero.
        var metrics = CodeInsightMetricCalculator.Compute(new CodeInsightMetricInputs(0, 0, 0, 0, Misses: 3));

        Assert.Null(metrics.Precision);
        Assert.Equal(0d, metrics.Recall);
        Assert.Null(metrics.F1);
        Assert.Null(metrics.AcceptanceRate);
    }

    [Fact]
    public void OnlyFalsePositivesGivesZeroPrecisionAndUndefinedRecall()
    {
        var metrics = CodeInsightMetricCalculator.Compute(new CodeInsightMetricInputs(0, 0, 0, FalsePositive: 3, Misses: 0));

        Assert.Equal(0d, metrics.Precision);
        Assert.Null(metrics.Recall);
        Assert.Null(metrics.F1);
        Assert.Equal(0d, metrics.AcceptanceRate);
    }

    [Fact]
    public void WrongAboutEverythingAndMissingThingsIsAZeroF1NotAnUndefinedOne()
    {
        // Both ratios are defined and both are zero. That is a real result, and it must be distinguishable from
        // having no data at all.
        var metrics = CodeInsightMetricCalculator.Compute(new CodeInsightMetricInputs(0, 0, 0, FalsePositive: 2, Misses: 2));

        Assert.Equal(0d, metrics.Precision);
        Assert.Equal(0d, metrics.Recall);
        Assert.Equal(0d, metrics.F1);
    }

    [Fact]
    public void APerfectReviewScoresOneOnEveryLens()
    {
        var metrics = CodeInsightMetricCalculator.Compute(
            new CodeInsightMetricInputs(Addressed: 5, Acknowledged: 0, Dismissed: 0, FalsePositive: 0, Misses: 0));

        Assert.Equal(1d, metrics.Precision);
        Assert.Equal(1d, metrics.Recall);
        Assert.Equal(1d, metrics.F1);
        Assert.Equal(1d, metrics.AcceptanceRate);
    }

    [Fact]
    public void AggregatingSumsTheInputsRatherThanAveragingTheRatios()
    {
        // The single most likely way to get this story quietly wrong. One pull request with one perfect finding
        // and one with ninety-nine half-right findings do not average to three quarters.
        var perfect = new CodeInsightMetricInputs(Addressed: 1, Acknowledged: 0, Dismissed: 0, FalsePositive: 0, Misses: 0);
        var poor = new CodeInsightMetricInputs(Addressed: 50, Acknowledged: 0, Dismissed: 0, FalsePositive: 49, Misses: 0);

        var aggregate = CodeInsightMetricCalculator.ComputeAggregate([perfect, poor]);

        var averagedRatios = (CodeInsightMetricCalculator.Compute(perfect).Precision!.Value
                              + CodeInsightMetricCalculator.Compute(poor).Precision!.Value) / 2d;

        Assert.Equal(51d / 100d, aggregate.Precision!.Value, 12);
        Assert.NotEqual(averagedRatios, aggregate.Precision!.Value, 3);
    }

    [Fact]
    public void AggregatingNothingIsUndefinedNotZero()
    {
        var aggregate = CodeInsightMetricCalculator.ComputeAggregate([]);

        Assert.Null(aggregate.F1);
        Assert.Null(aggregate.AcceptanceRate);
    }

    [Fact]
    public void TheInputsTravelWithTheResultSoItCanBeReDerived()
    {
        var inputs = new CodeInsightMetricInputs(3, 2, 1, 1, 4);

        var metrics = CodeInsightMetricCalculator.Compute(inputs);

        Assert.Equal(inputs, metrics.Inputs);
    }

    [Fact]
    public void RecomputingFromTheSameInputsIsBitForBitIdentical()
    {
        // The reproducibility criterion. It holds because the computation is pure and its operation order is
        // fixed; this test is what would catch someone introducing a clock, a culture, or a rounding step.
        var inputs = new CodeInsightMetricInputs(7, 3, 2, 5, 11);

        var first = CodeInsightMetricCalculator.Compute(inputs);
        var second = CodeInsightMetricCalculator.Compute(inputs);

        Assert.Equal(BitConverter.DoubleToInt64Bits(first.F1!.Value), BitConverter.DoubleToInt64Bits(second.F1!.Value));
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(first.AcceptanceRate!.Value),
            BitConverter.DoubleToInt64Bits(second.AcceptanceRate!.Value));
        Assert.Equal(first, second);
    }

    [Fact]
    public void SummingInputsIsOrderIndependent()
    {
        // Aggregation must not depend on the order pull requests happen to come back in.
        var parts = new[]
        {
            new CodeInsightMetricInputs(1, 2, 3, 4, 5),
            new CodeInsightMetricInputs(5, 4, 3, 2, 1),
            new CodeInsightMetricInputs(0, 1, 0, 1, 0),
        };

        var forward = CodeInsightMetricInputs.Sum(parts);
        var reversed = CodeInsightMetricInputs.Sum(parts.Reverse());

        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void TheAttributableLensesLeaveRecallUndefinedRatherThanPerfect()
    {
        // Grouped by producing model, a miss belongs to nobody. Counting it as zero would hand whichever model
        // happened to run a flawless recall, and the F1 built on it would be the most quoted number on the page.
        var metrics = CodeInsightMetricCalculator.ComputeAttributable(
            new CodeInsightMetricInputs(Addressed: 6, Acknowledged: 2, Dismissed: 1, FalsePositive: 3, Misses: 0));

        Assert.Equal(9d / 12d, metrics.Precision!.Value, 12);
        Assert.Equal(8d / 12d, metrics.AcceptanceRate!.Value, 12);
        Assert.Null(metrics.Recall);
        Assert.Null(metrics.F1);
    }

    [Fact]
    public void TheAttributableLensesDiscardMissesTheyWereHandedByMistake()
    {
        // The carried inputs are what a caller re-derives the ratios from, so they must not carry a count no
        // ratio here used.
        var metrics = CodeInsightMetricCalculator.ComputeAttributable(
            new CodeInsightMetricInputs(Addressed: 1, Acknowledged: 0, Dismissed: 0, FalsePositive: 1, Misses: 7));

        Assert.Equal(0, metrics.Inputs.Misses);
        Assert.Equal(0.5d, metrics.Precision!.Value, 12);
        Assert.Null(metrics.Recall);
    }

    [Fact]
    public void TheAttributableLensesAreUndefinedWhenNothingResolved()
    {
        var metrics = CodeInsightMetricCalculator.ComputeAttributable(default);

        Assert.Null(metrics.Precision);
        Assert.Null(metrics.AcceptanceRate);
        Assert.Null(metrics.Recall);
        Assert.Null(metrics.F1);
    }

    [Fact]
    public void AcceptanceRateNeedsNoMisses()
    {
        // It is the early signal: available continuously, before any pull request has closed and before recall
        // has anything to work with.
        var metrics = CodeInsightMetricCalculator.Compute(
            new CodeInsightMetricInputs(Addressed: 3, Acknowledged: 1, Dismissed: 1, FalsePositive: 1, Misses: 0));

        Assert.Equal(4d / 6d, metrics.AcceptanceRate!.Value, 12);
        Assert.NotNull(metrics.Precision);
    }
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.CodeInsights.Metrics;

namespace MeisterDev.ProPR.CodeInsights.Tests.Metrics;

public sealed class ReviewerPerformanceScoreCalculatorTests
{
    [Fact]
    public void PremisesKeepTheirOrderedInterpretationsAndNumericResults()
    {
        var score = ReviewerPerformanceScoreCalculator.Compute(new(new(8, 2, 3, 4, 1), new(2, 1, 1), ActedMisses: 2, UnactedMisses: 3));
        var rules = new[] { "positive", "negative", "excluded" };
        var expected = from dismissed in rules
            from wontFix in rules
            from byDesign in rules
            from misses in new[] { "acted", "allSettled" }
            from duplicates in new[] { "retain", "negative", "excluded" }
            select $"reviewer-performance-v1/dismissed:{dismissed}/wontFix:{wontFix}/byDesign:{byDesign}/misses:{misses}/duplicates:{duplicates}";

        Assert.Equal(expected, score.Scenarios.Select(scenario => scenario.Id));
        Assert.Equal(
            (16L, 2L, 2L, 0L),
            (score.Scenarios[0].TruePositives, score.Scenarios[0].FalsePositives, score.Scenarios[0].FalseNegatives!.Value, score.Scenarios[0].Excluded));
        Assert.Equal(
            (6L, 1L, 5L, 11L),
            (score.Scenarios[^1].TruePositives, score.Scenarios[^1].FalsePositives, score.Scenarios[^1].FalseNegatives!.Value, score.Scenarios[^1].Excluded));
        Assert.Equal(32d / 36, score.Scenarios[0].F1);
        Assert.Equal(12d / 18, score.Scenarios[^1].F1);
    }

    [Theory]
    [MemberData(nameof(NegativeCoverageCounts))]
    public void EveryNegativeEvidenceCountIsRejected(ReviewerPerformanceCounts counts)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReviewerPerformanceScoreCalculator.Compute(counts));
    }

    public static IEnumerable<object[]> NegativeCoverageCounts()
    {
        var counts = new ReviewerPerformanceCounts();
        yield return [counts with { ProvisionalMisses = -1 }];
        yield return [counts with { SuppressedRepeats = -1 }];
        yield return [counts with { Withheld = -1 }];
        yield return [counts with { PublicationUnknown = -1 }];
        yield return [counts with { DuplicateChecked = -1 }];
        yield return [counts with { DuplicateSuspected = -1 }];
        yield return [counts with { DuplicateUnknown = -1 }];
        yield return [counts with { Unclassified = -1 }];
        yield return [counts with { Unattributed = -1 }];
        yield return [counts with { HarvestedThreads = -1 }];
        yield return [counts with { FailedMissJudgements = -1 }];
    }

    [Fact]
    public void EveryPremiseHasAStableIdentityAndUsesTheSameInputCounts()
    {
        var counts = new ReviewerPerformanceCounts(new(8, 2, 3, 4, 1), new(2, 1, 1), ActedMisses: 2, UnactedMisses: 3);
        var result = ReviewerPerformanceScoreCalculator.Compute(counts);

        Assert.Equal(162, result.Scenarios.Count);
        Assert.Equal(162, result.Scenarios.Select(item => item.Id).Distinct().Count());
        Assert.Equal(counts, result.Counts);
        Assert.All(result.Scenarios, item => Assert.Equal(18, item.TruePositives + item.FalsePositives + item.Excluded));
        Assert.Equal(result.Scenarios.Select(item => item.Id), ReviewerPerformanceScoreCalculator.Compute(counts).Scenarios.Select(item => item.Id));
    }

    [Theory]
    [InlineData("retain", 11, 2, 0)]
    [InlineData("negative", 8, 5, 0)]
    [InlineData("excluded", 8, 1, 4)]
    public void DuplicateContributionReplacesItsUnderlyingOutcome(string rule, int tp, int fp, int excluded)
    {
        var result = ReviewerPerformanceScoreCalculator.Compute(new(new(8, 2, 3), new(2, 1, 1), ActedMisses: 2));
        var scenario = result.Scenarios.Single(item =>
            item.Dismissed == "positive" && item.WontFix == "positive" && item.ByDesign == "positive" && item.Misses == "acted" && item.Duplicates == rule);

        Assert.Equal(tp, scenario.TruePositives);
        Assert.Equal(fp, scenario.FalsePositives);
        Assert.Equal(excluded, scenario.Excluded);
        Assert.Equal(2, scenario.FalseNegatives);
        Assert.Equal(2d * tp / (2 * tp + fp + 2), scenario.F1);
    }

    [Fact]
    public void UnknownUnderlyingOutcomesStayExcludedWhenDuplicatesArePenalized()
    {
        var result = ReviewerPerformanceScoreCalculator.Compute(new(new(Positive: 1, Unknown: 3), new(Unknown: 2)));
        Assert.All(
            result.Scenarios, item =>
            {
                Assert.Equal(1, item.TruePositives);
                Assert.Equal(0, item.FalsePositives);
                Assert.Equal(3, item.Excluded);
            });
    }

    [Fact]
    public void MissingMissAttributionWithholdsRecallAndF1WithoutWithholdingPrecision()
    {
        var result = ReviewerPerformanceScoreCalculator.Compute(new(new(3, 1), default, ActedMisses: 2), recallIsMeasurable: false);
        Assert.All(
            result.Scenarios, item =>
            {
                Assert.Equal(0.75, item.Precision);
                Assert.Null(item.Recall);
                Assert.Null(item.F1);
            });
        Assert.Null(result.Summary.F1);
    }

    [Fact]
    public void EmptyDenominatorsAreUnavailableAndMeasuredZeroIsRetained()
    {
        Assert.All(
            ReviewerPerformanceScoreCalculator.Compute(default).Scenarios, item =>
            {
                Assert.Null(item.Precision);
                Assert.Null(item.Recall);
                Assert.Null(item.F1);
            });
        Assert.All(
            ReviewerPerformanceScoreCalculator.Compute(new(new(Wrong: 2), default, ActedMisses: 1)).Scenarios, item =>
            {
                Assert.Equal(0, item.Precision);
                Assert.Equal(0, item.Recall);
                Assert.Equal(0, item.F1);
            });
    }

    [Fact]
    public void QuartilesInterpolateAtTheSpecifiedEnumerationPosition()
    {
        var summary = ReviewerPerformanceScoreCalculator.Summarize([0d, 0.2, null, 0.8, 1]);
        Assert.NotNull(summary);
        Assert.Equal(0, summary.Minimum);
        Assert.Equal(0.15, summary.FirstQuartile, 12);
        Assert.Equal(0.5, summary.Median, 12);
        Assert.Equal(0.85, summary.ThirdQuartile, 12);
        Assert.Equal(1, summary.Maximum);
        Assert.Equal(4, summary.AvailableScenarios);
    }

    [Fact]
    public void InvalidDuplicateSubsetsAndNegativeCountsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReviewerPerformanceScoreCalculator.Compute(new(new(2), new(3))));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReviewerPerformanceScoreCalculator.Compute(new(new(-1), default)));
    }
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.CodeInsights.Metrics;

namespace MeisterDev.ProPR.Application.Tests.Features.CodeInsights;

/// <summary>
///     The trend test behind the direction shown beside every headline metric. The cases that matter are the ones
///     where a first-against-last comparison lies: a recovering series, a series with one odd period, and a series
///     that wandered without going anywhere.
/// </summary>
public sealed class CodeInsightTrendAnalyzerTests
{
    [Fact]
    public void ASeriesTooShortToTestIsReportedAsSuchRatherThanAsFlat()
    {
        var trend = CodeInsightTrendAnalyzer.Analyse([0.1, 0.2, 0.3, 0.4]);

        Assert.Equal(CodeInsightTrendVerdict.Insufficient, trend.Verdict);
        Assert.Equal(4, trend.Periods);
        // Nothing was tested, so nothing is reported: a Tau or a p-value here would be read as evidence.
        Assert.Null(trend.Tau);
        Assert.Null(trend.PValue);
        Assert.Null(trend.SlopePerPeriod);
    }

    [Fact]
    public void AMonotonicRiseIsSignificantAndCarriesItsSlope()
    {
        var trend = CodeInsightTrendAnalyzer.Analyse([0.50, 0.52, 0.54, 0.56, 0.58, 0.60, 0.62, 0.64]);

        Assert.Equal(CodeInsightTrendVerdict.Rising, trend.Verdict);
        // Every one of the 28 ordered pairs agrees, which is what Tau of 1 means.
        Assert.Equal(1d, trend.Tau!.Value, 12);
        Assert.True(trend.PValue < 0.05, $"p-value {trend.PValue} should clear the significance level");
        Assert.Equal(0.02d, trend.SlopePerPeriod!.Value, 12);
        Assert.Equal(8, trend.Periods);
    }

    [Fact]
    public void AMonotonicFallIsSignificantAndItsSlopeIsNegative()
    {
        var trend = CodeInsightTrendAnalyzer.Analyse([0.9, 0.8, 0.7, 0.6, 0.5, 0.4, 0.3, 0.2]);

        Assert.Equal(CodeInsightTrendVerdict.Falling, trend.Verdict);
        Assert.Equal(-1d, trend.Tau!.Value, 12);
        Assert.Equal(-0.1d, trend.SlopePerPeriod!.Value, 12);
    }

    [Fact]
    public void ASeriesThatFellAndRecoveredIsNotCalledRising()
    {
        // First against last says improving, from 0.50 to 0.55. Every period in between fell.
        var trend = CodeInsightTrendAnalyzer.Analyse([0.50, 0.44, 0.40, 0.37, 0.33, 0.30, 0.28, 0.55]);

        Assert.NotEqual(CodeInsightTrendVerdict.Rising, trend.Verdict);
        Assert.True(trend.Tau < 0, $"Tau {trend.Tau} should be negative for a mostly falling series");
    }

    [Fact]
    public void OneOutlyingPeriodDoesNotManufactureADirection()
    {
        // A flat series with a single spike. A mean-based or endpoint-based reading would move; a rank-based one
        // does not, because the spike is one pair out of 28 rather than a large number.
        var trend = CodeInsightTrendAnalyzer.Analyse([0.60, 0.60, 0.60, 0.60, 0.60, 0.60, 0.60, 0.95]);

        Assert.Equal(CodeInsightTrendVerdict.Flat, trend.Verdict);
        Assert.True(trend.PValue > 0.05, $"p-value {trend.PValue} should not clear the significance level");
    }

    [Fact]
    public void ANoisySeriesWithNoDirectionIsFlatRatherThanImproving()
    {
        var trend = CodeInsightTrendAnalyzer.Analyse([0.60, 0.66, 0.58, 0.71, 0.55, 0.68, 0.62, 0.64]);

        Assert.Equal(CodeInsightTrendVerdict.Flat, trend.Verdict);
        Assert.True(trend.PValue > 0.05, $"p-value {trend.PValue} should not clear the significance level");
    }

    [Fact]
    public void AnUnchangingSeriesIsFlatWithNothingLeftToTest()
    {
        var trend = CodeInsightTrendAnalyzer.Analyse([0.7, 0.7, 0.7, 0.7, 0.7, 0.7, 0.7, 0.7]);

        Assert.Equal(CodeInsightTrendVerdict.Flat, trend.Verdict);
        Assert.Equal(0d, trend.Tau);
        Assert.Equal(1d, trend.PValue);
        Assert.Equal(0d, trend.SlopePerPeriod);
    }

    [Fact]
    public void RepeatedValuesDoNotDefeatARealTrend()
    {
        // Ties are the normal case for a ratio computed from small counts, and they inflate the variance rather
        // than the statistic, so the test has to survive them.
        var trend = CodeInsightTrendAnalyzer.Analyse([0.2, 0.2, 0.3, 0.3, 0.4, 0.4, 0.5, 0.5, 0.6, 0.6]);

        Assert.Equal(CodeInsightTrendVerdict.Rising, trend.Verdict);
        Assert.True(trend.PValue < 0.05, $"p-value {trend.PValue} should clear the significance level");
        Assert.True(trend.Tau > 0.8, $"Tau {trend.Tau} should be high for a stepped rise");
    }

    [Fact]
    public void TheSlopeIsPerPeriodRatherThanAcrossTheWindow()
    {
        // Sen's slope divides each pairwise difference by the distance between the periods, so a series that
        // gained 0.35 over 8 periods reports 0.05 per period and not 0.35.
        var trend = CodeInsightTrendAnalyzer.Analyse([0.10, 0.15, 0.20, 0.25, 0.30, 0.35, 0.40, 0.45]);

        Assert.Equal(0.05d, trend.SlopePerPeriod!.Value, 12);
    }

    [Fact]
    public void TheTestIsSymmetricUnderReversal()
    {
        double[] rising = [0.31, 0.34, 0.36, 0.41, 0.44, 0.48, 0.53, 0.57];
        var forwards = CodeInsightTrendAnalyzer.Analyse(rising);
        var backwards = CodeInsightTrendAnalyzer.Analyse([.. rising.Reverse()]);

        Assert.Equal(CodeInsightTrendVerdict.Rising, forwards.Verdict);
        Assert.Equal(CodeInsightTrendVerdict.Falling, backwards.Verdict);
        Assert.Equal(forwards.PValue!.Value, backwards.PValue!.Value, 12);
        Assert.Equal(forwards.Tau!.Value, -backwards.Tau!.Value, 12);
        Assert.Equal(forwards.SlopePerPeriod!.Value, -backwards.SlopePerPeriod!.Value, 12);
    }

    [Fact]
    public void TheStatisticMatchesAHandCount()
    {
        // Nine ascending values with the last two swapped. Of the 36 ordered pairs, 35 rise and 1 falls, so
        // S = 34 and Tau = 34/36.
        var trend = CodeInsightTrendAnalyzer.Analyse([1, 2, 3, 4, 5, 6, 7, 9, 8]);

        Assert.Equal(34d / 36d, trend.Tau!.Value, 12);
        Assert.Equal(CodeInsightTrendVerdict.Rising, trend.Verdict);
    }

    [Fact]
    public void ANullSeriesIsRejectedRatherThanReadAsEmpty()
    {
        Assert.Throws<ArgumentNullException>(() => CodeInsightTrendAnalyzer.Analyse(null!));
    }
}

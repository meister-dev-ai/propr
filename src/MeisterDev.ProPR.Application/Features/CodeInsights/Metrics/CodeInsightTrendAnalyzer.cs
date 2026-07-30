// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Application.Features.CodeInsights.Metrics;

/// <summary>
///     Which way a series has moved across a window, and whether the movement survives a significance test. Rising
///     and falling rather than better and worse, because whether a rise is good news belongs to the metric and so
///     to the caller.
/// </summary>
public enum CodeInsightTrendVerdict
{
    /// <summary>Too few periods to test, which is the honest answer whenever the series cannot support a statement.</summary>
    Insufficient = 0,

    /// <summary>A statistically significant upward trend.</summary>
    Rising = 1,

    /// <summary>A statistically significant downward trend.</summary>
    Falling = 2,

    /// <summary>No significant trend. The series moved without moving in a way that survives a test.</summary>
    Flat = 3,
}

/// <summary>
///     A trend as a test result rather than as an arrow.
/// </summary>
/// <param name="Verdict">The verdict, a direction only where <paramref name="PValue" /> clears the threshold.</param>
/// <param name="Tau">
///     Kendall's Tau: how consistently the series moves one way, from -1 to 1. Independent of how large the moves
///     are, which is why it travels beside the slope rather than instead of it.
/// </param>
/// <param name="PValue">
///     Two-sided p-value of the Mann-Kendall test. Above the threshold the direction is reported as flat, however
///     large the slope looks.
/// </param>
/// <param name="SlopePerPeriod">
///     Sen's slope: the median change per period, in the metric's own units. This is the number worth quoting,
///     because Tau says how consistent a move is and nothing about how big.
/// </param>
/// <param name="Periods">Periods that carried enough sample to be included in the test.</param>
public sealed record CodeInsightTrend(
    CodeInsightTrendVerdict Verdict,
    double? Tau,
    double? PValue,
    double? SlopePerPeriod,
    int Periods)
{
    /// <summary>Nothing testable, from no periods at all or from too few to run the test on.</summary>
    public static CodeInsightTrend Insufficient(int periods = 0) =>
        new(CodeInsightTrendVerdict.Insufficient, null, null, null, periods);
}

/// <summary>
///     Tests whether a metric series trends, using Mann-Kendall with Kendall's Tau and Sen's slope.
/// </summary>
/// <remarks>
///     <para>
///         The obvious alternative, comparing the first period against the last, lets two points decide the verdict
///         and throws away everything between them. A window whose metric fell for eight weeks and recovered in the
///         ninth reads as improving, and one that rose steadily for eight weeks and dipped in the ninth reads as
///         declining. Mann-Kendall counts every ordered pair instead, so a direction has to hold across the series
///         to be reported, and it is rank-based, so a single outlying period cannot manufacture one.
///     </para>
///     <para>
///         This is the method the empirical literature on code-review feedback uses for exactly this question
///         (Kendall's Tau, the p-value, and Sen's slope reported together over monthly periods), which also makes
///         our numbers comparable with published ones.
///     </para>
///     <para>
///         Pure and deterministic: no clock, no database, no configuration. The p-value uses the normal
///         approximation with a tie correction, which needs a handful of periods before it means anything, hence
///         <see cref="MinimumPeriods" />.
///     </para>
/// </remarks>
public static class CodeInsightTrendAnalyzer
{
    /// <summary>
    ///     Periods a series needs before the test is run at all. The normal approximation to Mann-Kendall's S is
    ///     unreliable below this, and a p-value nobody can trust is worse than no verdict.
    /// </summary>
    public const int MinimumPeriods = 8;

    /// <summary>Significance threshold. Above this the movement is reported as flat rather than as a direction.</summary>
    public const double SignificanceLevel = 0.05;

    /// <summary>
    ///     Tests the ordered values of one metric. Callers pass only the periods that cleared their own sample
    ///     floor: a ratio computed from two closed pull requests is not evidence of anything, and dropping it here
    ///     rather than in the test keeps the sample rule in one place.
    /// </summary>
    public static CodeInsightTrend Analyse(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count < MinimumPeriods)
        {
            return CodeInsightTrend.Insufficient(values.Count);
        }

        var s = 0;
        var untiedPairs = 0;
        for (var i = 0; i < values.Count - 1; i++)
        {
            for (var j = i + 1; j < values.Count; j++)
            {
                var sign = Math.Sign(values[j] - values[i]);
                s += sign;
                if (sign != 0)
                {
                    untiedPairs++;
                }
            }
        }

        // A series that never changes is flat by construction rather than by test, and the variance correction for
        // ties would cancel the variance out entirely.
        if (untiedPairs == 0)
        {
            return new CodeInsightTrend(CodeInsightTrendVerdict.Flat, 0, 1, 0, values.Count);
        }

        var pairs = values.Count * (values.Count - 1) / 2.0;
        var pValue = PValue(s, values);

        var verdict = pValue > SignificanceLevel
            ? CodeInsightTrendVerdict.Flat
            : s > 0
                ? CodeInsightTrendVerdict.Rising
                : s < 0
                    ? CodeInsightTrendVerdict.Falling
                    : CodeInsightTrendVerdict.Flat;

        return new CodeInsightTrend(verdict, s / pairs, pValue, SensSlope(values), values.Count);
    }

    /// <summary>
    ///     Two-sided p-value from the normal approximation, with the continuity correction and the variance
    ///     correction for tied values that the test requires when a metric repeats a value across periods.
    /// </summary>
    private static double PValue(int s, IReadOnlyList<double> values)
    {
        var n = values.Count;
        var variance = (n * (n - 1.0) * (2 * n + 5.0) / 18.0) - TieCorrection(values);

        if (variance <= 0)
        {
            return 1;
        }

        // The continuity correction: S is a discrete statistic being read off a continuous distribution.
        var z = s > 0
            ? (s - 1) / Math.Sqrt(variance)
            : s < 0
                ? (s + 1) / Math.Sqrt(variance)
                : 0;

        return Math.Clamp(2 * (1 - StandardNormalCdf(Math.Abs(z))), 0, 1);
    }

    private static double TieCorrection(IReadOnlyList<double> values)
    {
        return values
            .GroupBy(value => value)
            .Where(group => group.Count() > 1)
            .Sum(group =>
            {
                double t = group.Count();
                return t * (t - 1) * (2 * t + 5) / 18.0;
            });
    }

    /// <summary>The median of every pairwise slope, which is what makes it robust to one odd period.</summary>
    private static double SensSlope(IReadOnlyList<double> values)
    {
        var slopes = new List<double>();
        for (var i = 0; i < values.Count - 1; i++)
        {
            for (var j = i + 1; j < values.Count; j++)
            {
                slopes.Add((values[j] - values[i]) / (j - i));
            }
        }

        slopes.Sort();
        var middle = slopes.Count / 2;

        return slopes.Count % 2 == 1
            ? slopes[middle]
            : (slopes[middle - 1] + slopes[middle]) / 2;
    }

    /// <summary>
    ///     Standard normal CDF via the error function, using Abramowitz and Stegun 7.1.26. Accurate to about
    ///     1.5e-7, which is far tighter than a p-value compared against 0.05 needs.
    /// </summary>
    private static double StandardNormalCdf(double z) => 0.5 * (1 + Erf(z / Math.Sqrt(2)));

    private static double Erf(double x)
    {
        var sign = Math.Sign(x);
        x = Math.Abs(x);

        const double a1 = 0.254829592;
        const double a2 = -0.284496736;
        const double a3 = 1.421413741;
        const double a4 = -1.453152027;
        const double a5 = 1.061405429;
        const double p = 0.3275911;

        var t = 1 / (1 + (p * x));
        var y = 1 - (((((((((a5 * t) + a4) * t) + a3) * t) + a2) * t) + a1) * t * Math.Exp(-x * x));

        return sign * y;
    }
}

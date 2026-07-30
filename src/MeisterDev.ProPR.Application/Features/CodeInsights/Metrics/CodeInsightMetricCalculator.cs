// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Application.Features.CodeInsights.Metrics;

/// <summary>
///     The counted inputs both metric lenses are computed from. Everything else is derived, so these are what
///     a seal stores and what an aggregate sums.
/// </summary>
/// <param name="Addressed">Findings whose claimed fix was corroborated by a code change.</param>
/// <param name="Acknowledged">Findings a human accepted without changing the code.</param>
/// <param name="Dismissed">Findings judged correct but not wanted here.</param>
/// <param name="FalsePositive">Findings judged wrong.</param>
/// <param name="Misses">
///     Human-raised issues the reviewer did not raise and that qualified as something it should have caught.
///     Only qualifying misses; the other harvested rows exist to make the cut-off inspectable.
/// </param>
/// <param name="Discussed">
///     Findings a human engaged with and left unresolved: a reply, an argument, a question, and then no verdict
///     and no code change. Counted so the volume is visible, and deliberately in neither ratio. A thread that
///     fizzled is not evidence that a finding was right, nor evidence that it was unwanted, and forcing it into
///     either lens would put a number nobody established into both.
/// </param>
public readonly record struct CodeInsightMetricInputs(
    int Addressed,
    int Acknowledged,
    int Dismissed,
    int FalsePositive,
    int Misses,
    int Discussed = 0)
{
    /// <summary>
    ///     Findings the reviewer was right about. Dismissed counts here: it was a correct finding the team chose
    ///     not to act on. This is deliberately different from the acceptance lens, where dismissed is not
    ///     accepted: the two lenses disagreeing about the same finding is the point of having both.
    /// </summary>
    public int TruePositives => this.Addressed + this.Acknowledged + this.Dismissed;

    /// <summary>Findings the reviewer was wrong about.</summary>
    public int FalsePositives => this.FalsePositive;

    /// <summary>Issues the reviewer should have raised and did not.</summary>
    public int FalseNegatives => this.Misses;

    /// <summary>Findings that reached an outcome. The acceptance lens's denominator.</summary>
    public int Resolved =>
        this.Addressed + this.Acknowledged + this.Dismissed + this.FalsePositive;

    /// <summary>Findings a human acted on or agreed with.</summary>
    public int Accepted => this.Addressed + this.Acknowledged;

    /// <summary>Adds two input sets. How a pull-request result rolls up to a repository or a client.</summary>
    public static CodeInsightMetricInputs operator +(CodeInsightMetricInputs left, CodeInsightMetricInputs right)
    {
        return new CodeInsightMetricInputs(
            left.Addressed + right.Addressed,
            left.Acknowledged + right.Acknowledged,
            left.Dismissed + right.Dismissed,
            left.FalsePositive + right.FalsePositive,
            left.Misses + right.Misses,
            left.Discussed + right.Discussed);
    }

    /// <summary>Sums a sequence of input sets, for aggregating many pull requests into one result.</summary>
    public static CodeInsightMetricInputs Sum(IEnumerable<CodeInsightMetricInputs> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        var total = default(CodeInsightMetricInputs);
        foreach (var part in parts)
        {
            total += part;
        }

        return total;
    }
}

/// <summary>
///     Both lenses over one set of inputs. Every ratio is nullable, and <see langword="null" /> means
///     <em>undefined</em> rather than zero: there was nothing to divide by. A metric that silently reports 0
///     for "nothing happened" is a lie a chart will draw as a collapse in quality.
/// </summary>
/// <param name="Inputs">The counts the ratios were derived from, carried so a result can be re-derived.</param>
/// <param name="Precision">Of the findings the reviewer raised and that resolved, how many were right.</param>
/// <param name="Recall">Of the issues that were there to find, how many the reviewer found.</param>
/// <param name="F1">Harmonic mean of precision and recall.</param>
/// <param name="AcceptanceRate">Of the findings that resolved, how many a human acted on or agreed with.</param>
public readonly record struct CodeInsightMetrics(
    CodeInsightMetricInputs Inputs,
    double? Precision,
    double? Recall,
    double? F1,
    double? AcceptanceRate);

/// <summary>
///     Turns counted outcomes into the two metric lenses.
/// </summary>
/// <remarks>
///     Pure and I/O-free by design: reproducing a metric from its stored inputs is an acceptance criterion, and
///     that is only trustworthy if the computation has nothing else to depend on, no clock, no database, no
///     configuration.
/// </remarks>
public static class CodeInsightMetricCalculator
{
    /// <summary>Computes both lenses over <paramref name="inputs" />.</summary>
    public static CodeInsightMetrics Compute(CodeInsightMetricInputs inputs)
    {
        var precision = Ratio(inputs.TruePositives, inputs.TruePositives + inputs.FalsePositives);
        var recall = Ratio(inputs.TruePositives, inputs.TruePositives + inputs.FalseNegatives);

        return new CodeInsightMetrics(
            inputs,
            precision,
            recall,
            HarmonicMean(precision, recall),
            Ratio(inputs.Accepted, inputs.Resolved));
    }

    /// <summary>
    ///     Computes only the lenses that can be attributed to whichever pass or model produced a finding:
    ///     precision and acceptance. Recall and F1 come back <see langword="null" />.
    /// </summary>
    /// <remarks>
    ///     A miss is a problem a human raised and no finding of ours describes, so there is no producing model to
    ///     charge it to. Splitting the misses across the models that happened to run would invent the number, and
    ///     counting them as zero would hand the cheapest model a perfect recall it never earned. Undefined is the
    ///     only honest answer, and it is what a view must render as "—".
    /// </remarks>
    public static CodeInsightMetrics ComputeAttributable(CodeInsightMetricInputs inputs)
    {
        return new CodeInsightMetrics(
            inputs with { Misses = 0 },
            Ratio(inputs.TruePositives, inputs.TruePositives + inputs.FalsePositives),
            Recall: null,
            F1: null,
            Ratio(inputs.Accepted, inputs.Resolved));
    }

    /// <summary>
    ///     Computes both lenses over the sum of many input sets: the only correct way to roll a metric up.
    /// </summary>
    /// <remarks>
    ///     Summing the inputs and computing once is not the same as averaging the ratios, and the difference is
    ///     not small: one pull request with a single perfect finding and one with ninety-nine half-right findings
    ///     do not average to three quarters in any sense an operator would accept.
    /// </remarks>
    public static CodeInsightMetrics ComputeAggregate(IEnumerable<CodeInsightMetricInputs> parts)
    {
        return Compute(CodeInsightMetricInputs.Sum(parts));
    }

    /// <summary>
    ///     Returns <paramref name="numerator" /> over <paramref name="denominator" />, or
    ///     <see langword="null" /> when there is nothing to divide by.
    /// </summary>
    private static double? Ratio(int numerator, int denominator)
    {
        return denominator == 0 ? null : (double)numerator / denominator;
    }

    /// <summary>
    ///     Harmonic mean of two ratios. Undefined when either input is undefined; zero when both are zero,
    ///     which is a real result (the reviewer was wrong about everything and missed things) rather than an
    ///     absence of one.
    /// </summary>
    private static double? HarmonicMean(double? left, double? right)
    {
        if (left is null || right is null)
        {
            return null;
        }

        var sum = left.Value + right.Value;
        return sum == 0d ? 0d : 2d * left.Value * right.Value / sum;
    }
}

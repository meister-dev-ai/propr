// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.CodeInsights.Metrics;

/// <summary>Evaluates supported interpretations without I/O.</summary>
public static class ReviewerPerformanceScoreCalculator
{
    public const string Version = "reviewer-performance-v1";

    public static ReviewerPerformanceScore Compute(ReviewerPerformanceCounts counts, bool recallIsMeasurable = true)
    {
        var outcomes = counts.Outcomes.Values();
        var duplicates = counts.ConfirmedDuplicates.Values();
        ValidateCounts(counts, outcomes, duplicates);
        var scenarios = new List<ReviewerPerformanceScenario>(ReviewerPerformancePremises.All.Count);
        foreach (var premise in ReviewerPerformancePremises.All)
        {
            scenarios.Add(Evaluate(premise, counts, outcomes, duplicates, recallIsMeasurable));
        }

        return new(
            counts, scenarios.AsReadOnly(), new(
                Summarize(scenarios.Select(item => item.Precision)),
                Summarize(scenarios.Select(item => item.Recall)),
                Summarize(scenarios.Select(item => item.F1))));
    }

    private static void ValidateCounts(ReviewerPerformanceCounts counts, long[] outcomes, long[] duplicates)
    {
        var coverage = new[]
        {
            counts.ActedMisses, counts.UnactedMisses, counts.ProvisionalMisses, counts.Generated,
            counts.SuppressedRepeats, counts.Withheld, counts.PublicationUnknown, counts.DuplicateChecked,
            counts.DuplicateSuspected, counts.DuplicateUnknown, counts.Unclassified, counts.Unattributed,
            counts.HarvestedThreads, counts.FailedMissJudgements
        };
        var invalidOutcomes = outcomes.Any(value => value < 0);
        var invalidDuplicates = duplicates.Where((value, index) => value < 0 || value > outcomes[index]).Any();
        if (invalidOutcomes || invalidDuplicates || coverage.Any(value => value < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(counts), "Counts must be nonnegative and duplicate subsets cannot exceed their outcome counts.");
        }
    }

    private static ReviewerPerformanceScenario Evaluate(
        ReviewerPerformancePremise premise, ReviewerPerformanceCounts counts, long[] outcomes, long[] duplicates, bool recallIsMeasurable)
    {
        long truePositives = 0;
        long falsePositives = 0;
        long excluded = 0;
        for (var index = 0; index < outcomes.Length; index++)
        {
            var rule = premise.OutcomeRules[index];
            var duplicateContribution = IsUnsettledOutcome(index) || premise.Duplicates == "retain" ? rule : premise.Duplicates;
            Contribute(rule, outcomes[index] - duplicates[index], ref truePositives, ref falsePositives, ref excluded);
            Contribute(duplicateContribution, duplicates[index], ref truePositives, ref falsePositives, ref excluded);
        }

        var falseNegatives = checked(counts.ActedMisses + (premise.Misses == "allSettled" ? counts.UnactedMisses : 0));
        return new(
            premise.Id, premise.Dismissed, premise.WontFix, premise.ByDesign, premise.Misses, premise.Duplicates,
            truePositives, falsePositives, recallIsMeasurable ? falseNegatives : null, excluded,
            Ratio(truePositives, checked(truePositives + falsePositives)),
            recallIsMeasurable ? Ratio(truePositives, checked(truePositives + falseNegatives)) : null,
            recallIsMeasurable ? Ratio(2d * truePositives, 2d * truePositives + falsePositives + falseNegatives) : null);
    }

    private static bool IsUnsettledOutcome(int index)
    {
        // The count vector ends with unknown and unresolved outcomes, which have no duplicate publication credit.
        return (OutcomePosition)index is OutcomePosition.Unknown or OutcomePosition.Unresolved;
    }

    private enum OutcomePosition
    {
        Positive,
        Wrong,
        Dismissed,
        WontFix,
        ByDesign,
        Unknown,
        Unresolved
    }

    /// <summary>Summarizes available values using linear interpolation at (n - 1) times q.</summary>
    public static ReviewerPerformanceRange? Summarize(IEnumerable<double?> source)
    {
        var values = source.Where(value => value is not null).Select(value => value!.Value).Order().ToArray();
        if (values.Length == 0)
        {
            return null;
        }

        double Quantile(double q)
        {
            var index = (values.Length - 1) * q;
            var lower = (int)Math.Floor(index);
            var upper = (int)Math.Ceiling(index);
            return values[lower] + (values[upper] - values[lower]) * (index - lower);
        }

        return new(values[0], Quantile(0.25), Quantile(0.5), Quantile(0.75), values[^1], values.Length);
    }

    private static void Contribute(string rule, long count, ref long truePositives, ref long falsePositives, ref long excluded)
    {
        switch (rule)
        {
            case "positive":
                truePositives = checked(truePositives + count);
                break;
            case "negative":
                falsePositives = checked(falsePositives + count);
                break;
            default:
                excluded = checked(excluded + count);
                break;
        }
    }

    private static double? Ratio(double numerator, double denominator) => denominator == 0 ? null : numerator / denominator;
}

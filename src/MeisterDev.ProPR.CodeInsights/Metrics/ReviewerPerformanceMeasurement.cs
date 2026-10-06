// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.CodeInsights.Http;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.CodeInsights.Metrics;

internal static class ReviewerPerformanceMeasurement
{
    internal static ReviewerPerformancePoint WithoutPeriodObservation(ReviewerPerformancePoint measurement) => measurement with
    {
        Score = measurement.Score with
        {
            Scenarios = measurement.Score.Scenarios.Select(scenario => scenario with
            {
                Precision = null,
                Recall = null,
                F1 = null
            }).ToArray(),
            Summary = new(null, null, null)
        },
        UnavailableReasons = measurement.UnavailableReasons.Concat(["no-series-observation-in-period"]).ToArray(),
    };

    internal static ReviewerPerformancePoint Measure(
        DateOnly date, IReadOnlyList<ReviewerPerformanceDailyCount> evidence, IReadOnlyList<ReviewerPerformanceDailyCount> population,
        ReviewerPerformanceViewQuery scope, string grouping)
    {
        var counts = default(ReviewerPerformanceCounts);
        foreach (var row in evidence.Where(row => !IsCoverage(row)))
        {
            counts += Vector(row);
        }

        var reasons = new List<string>();
        var misses = population.Where(row => row.IsMiss && row.Outcome != "missExcluded").ToList();
        var settledMisses = misses.Where(row => row.Outcome != "missProvisional").ToList();
        if (!population.Any(row => row.Outcome is "collectionComplete" or "collectionProvisional") || population.Any(row => row.Outcome == "collectionUnknown"))
        {
            reasons.Add("miss-collection-coverage-unavailable");
        }

        if (population.Any(row => row.Outcome == "collectionProvisional") || misses.Any(row => row.Outcome == "missProvisional"))
        {
            reasons.Add("miss-observation-provisional");
        }

        if (misses.Any(row => row.Outcome == "missFailed"))
        {
            reasons.Add("miss-judgement-failed");
        }

        if (scope.Models is not null || grouping == "model")
        {
            reasons.Add("miss-model-attribution-unavailable");
        }

        if ((scope.Types is not null || grouping == "type") && settledMisses.Any(row => row.TypeMembership.Length == 0 || !row.IsClassified))
        {
            reasons.Add("miss-type-attribution-unavailable");
        }

        if ((scope.Qualifiers is not null || grouping == "qualifier") && settledMisses.Any(row => row.Qualifier.Length == 0 || !row.IsClassified))
        {
            reasons.Add("miss-qualifier-attribution-unavailable");
        }

        if (settledMisses.Any(row => row.ProviderScope.Length == 0))
        {
            reasons.Add("miss-provider-scope-unavailable");
        }

        if (counts.PublicationUnknown > 0)
        {
            reasons.Add("publication-identity-unavailable");
        }

        if (counts.DuplicateUnknown > 0 || counts.DuplicateSuspected > 0)
        {
            reasons.Add("duplicate-verification-incomplete");
        }

        if (counts.Unclassified > 0)
        {
            reasons.Add("finding-classification-incomplete");
        }

        var recallAvailable = !reasons.Any(reason => reason.StartsWith("miss-", StringComparison.Ordinal) && reason != "miss-observation-provisional");
        return new(date, ReviewerPerformanceScoreCalculator.Compute(counts, recallAvailable), reasons);
    }

    private static ReviewerPerformanceCounts Vector(ReviewerPerformanceDailyCount row)
    {
        var n = row.Count;
        if (row.IsMiss)
        {
            return new(
                default, default, ActedMisses: row.Outcome == "missActed" ? n : 0,
                UnactedMisses: row.Outcome == "missUnacted" ? n : 0, ProvisionalMisses: row.Outcome == "missProvisional" ? n : 0,
                HarvestedThreads: n, FailedMissJudgements: row.Outcome == "missFailed" ? n : 0);
        }

        var published = row.PublicationState == CodeInsightPublicationState.Published;
        var outcomes = !published
            ? default
            : row.Outcome switch
            {
                "positive" => new ReviewerPerformanceOutcomeCounts(Positive: n),
                "wrong" => new(Wrong: n),
                "dismissed" => new(Dismissed: n),
                "wontFix" => new(WontFix: n),
                "byDesign" => new(ByDesign: n),
                "unresolved" => new(Unresolved: n),
                _ => new(Unknown: n),
            };
        return new(
            outcomes, row.DuplicateState == CodeInsightDuplicateState.ConfirmedDuplicate ? outcomes : default,
            Generated: n, SuppressedRepeats: row.PublicationState == CodeInsightPublicationState.SuppressedRepeat ? n : 0,
            Withheld: row.PublicationState is CodeInsightPublicationState.PolicyWithheld or CodeInsightPublicationState.Shadow
                or CodeInsightPublicationState.PostingDisabled or CodeInsightPublicationState.Failed
                ? n
                : 0,
            PublicationUnknown: row.PublicationState == CodeInsightPublicationState.Unknown ? n : 0,
            DuplicateChecked: published && row.DuplicateState is CodeInsightDuplicateState.CheckedNonduplicate or CodeInsightDuplicateState.ConfirmedDuplicate
                ? n
                : 0,
            DuplicateSuspected: published && row.DuplicateState == CodeInsightDuplicateState.Suspected ? n : 0,
            DuplicateUnknown: published && row.DuplicateState == CodeInsightDuplicateState.Unknown ? n : 0,
            Unclassified: !row.IsClassified ? n : 0, Unattributed: row.ModelId.Length == 0 ? n : 0);
    }

    internal static bool IsCoverage(ReviewerPerformanceDailyCount row) => row.Outcome.StartsWith("collection", StringComparison.Ordinal);
}

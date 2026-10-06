// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.CodeInsights.Metrics;

/// <summary>One immutable set of contribution rules in the versioned catalogue.</summary>
internal sealed record ReviewerPerformancePremise(
    string Id,
    string Dismissed,
    string WontFix,
    string ByDesign,
    string Misses,
    string Duplicates,
    IReadOnlyList<string> OutcomeRules);

internal static class ReviewerPerformancePremises
{
    private static readonly string[] ContributionRules = ["positive", "negative", "excluded"];
    private static readonly string[] MissRules = ["acted", "allSettled"];
    private static readonly string[] DuplicateRules = ["retain", "negative", "excluded"];

    internal static IReadOnlyList<ReviewerPerformancePremise> All { get; } = Build();

    private static IReadOnlyList<ReviewerPerformancePremise> Build()
    {
        var premiseCount = ContributionRules.Length * ContributionRules.Length * ContributionRules.Length
                           * MissRules.Length * DuplicateRules.Length;
        var premises = new List<ReviewerPerformancePremise>(premiseCount);
        foreach (var dismissed in ContributionRules)
        {
            foreach (var wontFix in ContributionRules)
            {
                foreach (var byDesign in ContributionRules)
                {
                    AddMissAndDuplicatePremises(premises, dismissed, wontFix, byDesign);
                }
            }
        }

        return premises.AsReadOnly();
    }

    private static void AddMissAndDuplicatePremises(List<ReviewerPerformancePremise> premises, string dismissed, string wontFix, string byDesign)
    {
        var outcomes = Array.AsReadOnly(new[] { "positive", "negative", dismissed, wontFix, byDesign, "excluded", "excluded" });
        foreach (var misses in MissRules)
        {
            foreach (var duplicates in DuplicateRules)
            {
                var id =
                    $"{ReviewerPerformanceScoreCalculator.Version}/dismissed:{dismissed}/wontFix:{wontFix}/byDesign:{byDesign}/misses:{misses}/duplicates:{duplicates}";
                premises.Add(new(id, dismissed, wontFix, byDesign, misses, duplicates, outcomes));
            }
        }
    }
}

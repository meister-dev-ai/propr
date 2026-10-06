// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.CodeInsights.Metrics;

/// <summary>One versioned interpretation applied to one count vector.</summary>
/// <param name="FalseNegatives">Eligible FN count, or null when compatible miss coverage is unavailable.</param>
public sealed record ReviewerPerformanceScenario(
    string Id,
    string Dismissed,
    string WontFix,
    string ByDesign,
    string Misses,
    string Duplicates,
    long TruePositives,
    long FalsePositives,
    long? FalseNegatives,
    long Excluded,
    double? Precision,
    double? Recall,
    double? F1);

/// <summary>Uniformly weighted enumeration summaries, without a probability or confidence interpretation.</summary>
public sealed record ReviewerPerformanceRange(
    double Minimum,
    double FirstQuartile,
    double Median,
    double ThirdQuartile,
    double Maximum,
    int AvailableScenarios);

/// <summary>Summaries of the coherent scenario tuples.</summary>
public sealed record ReviewerPerformanceSummary(ReviewerPerformanceRange? Precision, ReviewerPerformanceRange? Recall, ReviewerPerformanceRange? F1);

/// <summary>Immutable source inputs and their scenario results.</summary>
public sealed record ReviewerPerformanceScore(
    ReviewerPerformanceCounts Counts,
    IReadOnlyList<ReviewerPerformanceScenario> Scenarios,
    ReviewerPerformanceSummary Summary);

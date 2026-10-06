// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.CodeInsights.Metrics;

/// <summary>Disjoint published outcomes; duplicate counts use the same fields as subsets.</summary>
public readonly record struct ReviewerPerformanceOutcomeCounts(
    long Positive = 0,
    long Wrong = 0,
    long Dismissed = 0,
    long WontFix = 0,
    long ByDesign = 0,
    long Unknown = 0,
    long Unresolved = 0)
{
    /// <summary>Total publications represented by these counts.</summary>
    public long Total => checked(this.Positive + this.Wrong + this.Dismissed + this.WontFix + this.ByDesign + this.Unknown + this.Unresolved);

    /// <summary>Adds independent populations.</summary>
    public static ReviewerPerformanceOutcomeCounts operator +(ReviewerPerformanceOutcomeCounts a, ReviewerPerformanceOutcomeCounts b) =>
        new(
            checked(a.Positive + b.Positive), checked(a.Wrong + b.Wrong), checked(a.Dismissed + b.Dismissed),
            checked(a.WontFix + b.WontFix), checked(a.ByDesign + b.ByDesign), checked(a.Unknown + b.Unknown), checked(a.Unresolved + b.Unresolved));

    internal long[] Values() => [this.Positive, this.Wrong, this.Dismissed, this.WontFix, this.ByDesign, this.Unknown, this.Unresolved];
}

/// <summary>Retained inputs and evidence coverage for one scoped measurement.</summary>
public readonly record struct ReviewerPerformanceCounts(
    ReviewerPerformanceOutcomeCounts Outcomes,
    ReviewerPerformanceOutcomeCounts ConfirmedDuplicates,
    long ActedMisses = 0,
    long UnactedMisses = 0,
    long ProvisionalMisses = 0,
    long Generated = 0,
    long SuppressedRepeats = 0,
    long Withheld = 0,
    long PublicationUnknown = 0,
    long DuplicateChecked = 0,
    long DuplicateSuspected = 0,
    long DuplicateUnknown = 0,
    long Unclassified = 0,
    long Unattributed = 0,
    long HarvestedThreads = 0,
    long FailedMissJudgements = 0)
{
    /// <summary>Adds count vectors before scoring.</summary>
    public static ReviewerPerformanceCounts operator +(ReviewerPerformanceCounts a, ReviewerPerformanceCounts b) => new(
        a.Outcomes + b.Outcomes, a.ConfirmedDuplicates + b.ConfirmedDuplicates,
        checked(a.ActedMisses + b.ActedMisses), checked(a.UnactedMisses + b.UnactedMisses), checked(a.ProvisionalMisses + b.ProvisionalMisses),
        checked(a.Generated + b.Generated), checked(a.SuppressedRepeats + b.SuppressedRepeats), checked(a.Withheld + b.Withheld),
        checked(a.PublicationUnknown + b.PublicationUnknown),
        checked(a.DuplicateChecked + b.DuplicateChecked), checked(a.DuplicateSuspected + b.DuplicateSuspected),
        checked(a.DuplicateUnknown + b.DuplicateUnknown),
        checked(a.Unclassified + b.Unclassified), checked(a.Unattributed + b.Unattributed), checked(a.HarvestedThreads + b.HarvestedThreads),
        checked(a.FailedMissJudgements + b.FailedMissJudgements));
}

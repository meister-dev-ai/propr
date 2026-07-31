// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.CodeInsights;

/// <summary>
///     Every setting the Code Insights background work reads, in one place.
/// </summary>
/// <remarks>
///     <para>
///         Bound once and consumed through <c>IOptionsMonitor</c>, so a worker sees a changed value on its next
///         sweep instead of at the next restart, and no worker reaches into configuration by key.
///     </para>
///     <para>
///         Each value that would misbehave at zero or below carries its floor here rather than at the call site.
///         A misconfigured retention window that purges what was just collected, or a zero interval that turns a
///         sweep into a spin, are the failures these floors exist for, and they belong next to the value they
///         constrain.
///     </para>
/// </remarks>
public sealed class CodeInsightsOptions
{
    /// <summary>How often the classification sweeper drains its backlog. Floored at ten seconds.</summary>
    public int ClassificationIntervalSeconds { get; set; } = 60;

    /// <summary>How often the catch-up worker projects and seals what earlier runs missed. Floored at ten minutes.</summary>
    public int CatchUpIntervalSeconds { get; set; } = 21600;

    /// <summary>How often quality conditions are evaluated. Floored at five minutes.</summary>
    public int ConditionIntervalSeconds { get; set; } = 3600;

    /// <summary>How often elapsed collected data is purged. Floored at one minute.</summary>
    public int PurgeIntervalSeconds { get; set; } = 3600;

    /// <summary>How long collected data is kept, in days. Floored at one day.</summary>
    public int RetentionDays { get; set; } = 365;

    /// <summary>Jobs whose roll-ups one catch-up sweep may project. Floored at one.</summary>
    public int BackfillMaxJobs { get; set; } = 50;

    /// <summary>Pull requests one catch-up sweep may seal.</summary>
    public int SealSweepMaxPullRequests { get; set; } = 25;

    /// <summary>How long a pull request must be idle before a sweep seals it.</summary>
    public int SealSweepIdleDays { get; set; } = 7;

    /// <summary>Window the quality conditions are evaluated over, in days. Floored at one.</summary>
    public int ConditionWindowDays { get; set; } = 28;

    /// <summary>Drop in F1 across the window that counts as declining correctness.</summary>
    public double F1DeclineThreshold { get; set; } = 0.10;

    /// <summary>Share of findings judged wrong that counts as a high false-positive share.</summary>
    public double FalsePositiveShareThreshold { get; set; } = 0.30;

    /// <summary>Findings on one file that count as a concentration hotspot.</summary>
    public int ConcentrationThreshold { get; set; } = 25;

    /// <summary>
    ///     Sealed pull requests a correctness metric needs before it is presented as precise rather than as an
    ///     annotation. Floored at one. Provisional until a classifier evaluation calibrates it.
    /// </summary>
    public int MinimumSealedPullRequests { get; set; } = 10;

    /// <summary>The classification interval with its floor applied.</summary>
    public TimeSpan ClassificationInterval => AtLeast(this.ClassificationIntervalSeconds, 10);

    /// <summary>The catch-up interval with its floor applied.</summary>
    public TimeSpan CatchUpInterval => AtLeast(this.CatchUpIntervalSeconds, 600);

    /// <summary>The condition-evaluation interval with its floor applied.</summary>
    public TimeSpan ConditionInterval => AtLeast(this.ConditionIntervalSeconds, 300);

    /// <summary>The purge interval with its floor applied.</summary>
    public TimeSpan PurgeInterval => AtLeast(this.PurgeIntervalSeconds, 60);

    /// <summary>The retention window with its floor applied, so a zero cannot purge fresh data.</summary>
    public TimeSpan RetentionWindow => TimeSpan.FromDays(Math.Max(this.RetentionDays, 1));

    /// <summary>Jobs per catch-up sweep, floored at one so a sweep always makes progress.</summary>
    public int EffectiveBackfillMaxJobs => Math.Max(this.BackfillMaxJobs, 1);

    /// <summary>The condition window, floored at one day.</summary>
    public int EffectiveConditionWindowDays => Math.Max(this.ConditionWindowDays, 1);

    /// <summary>The sample floor, floored at one: a metric over nothing is undefined rather than precise.</summary>
    public int EffectiveMinimumSealedPullRequests => Math.Max(this.MinimumSealedPullRequests, 1);

    private static TimeSpan AtLeast(int seconds, int floorSeconds)
    {
        return TimeSpan.FromSeconds(Math.Max(seconds, floorSeconds));
    }
}

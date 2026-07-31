// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     What the thread-memory keyword back-fill reads.
/// </summary>
/// <remarks>
///     <para>
///         Keywords are extracted as memories are stored, so this back-fill exists only for memories written
///         before extraction did. It is off by default: every row it touches costs one model call, and an
///         installation that does not want to pay for its history should not have to switch anything off.
///     </para>
///     <para>
///         Consumed through <c>IOptionsMonitor</c>, so a budget raised while the host runs applies to the next
///         sweep rather than at the next restart.
///     </para>
/// </remarks>
public sealed class ThreadMemoryKeywordOptions
{
    /// <summary>Memories one sweep may back-fill. Zero, the default, leaves the back-fill off.</summary>
    public int BackfillMax { get; set; }

    /// <summary>How often the back-fill sweeps, in seconds. Floored at five minutes.</summary>
    public int SweepIntervalSeconds { get; set; } = 21600;

    /// <summary>Memories per sweep, never negative.</summary>
    public int EffectiveBackfillMax => Math.Max(this.BackfillMax, 0);

    /// <summary>The sweep interval with its floor applied, so a misconfigured zero cannot spin.</summary>
    public TimeSpan SweepInterval => TimeSpan.FromSeconds(Math.Max(this.SweepIntervalSeconds, 300));
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.ComponentModel.DataAnnotations;

namespace MeisterDev.ProPR.Application.Options;

/// <summary>
///     What counts as a live runner fleet, and how long a queue may sit still before that is called a
///     stall rather than a quiet moment.
/// </summary>
public sealed class RunnerFleetOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "RunnerFleet";

    /// <summary>
    ///     How recently a runner must have been heard from to count as active. Shorter than the reclaim
    ///     window on purpose: a runner that has gone quiet should stop counting as capacity well before its
    ///     leases are taken back, or the queue looks staffed while nothing is working.
    /// </summary>
    [Range(15, 3600, ErrorMessage = "ActiveHeartbeatWindowSeconds must be between 15 and 3600.")]
    public int ActiveHeartbeatWindowSeconds { get; set; } = 120;

    /// <summary>
    ///     How long the fleet must be continuously empty before in-process execution resumes.
    ///     <para>
    ///         This is the hysteresis. Without it a runner whose heartbeat flaps around the window would
    ///         toggle the whole installation between distributed and in-process execution every poll, and
    ///         the isolation guarantee would hold only between flaps.
    ///     </para>
    /// </summary>
    [Range(0, 3600, ErrorMessage = "FleetEmptySettleSeconds must be between 0 and 3600.")]
    public int FleetEmptySettleSeconds { get; set; } = 300;

    /// <summary>
    ///     How long jobs may sit pending with no runner able to take them before the queue is reported as
    ///     stalled. Long enough that an ordinary busy pool does not trip it, short enough that an operator
    ///     hears about an offline fleet in the same working session.
    /// </summary>
    [Range(30, 86400, ErrorMessage = "QueueStallGraceSeconds must be between 30 and 86400.")]
    public int QueueStallGraceSeconds { get; set; } = 600;

    /// <summary>The active-heartbeat window as a <see cref="TimeSpan" />.</summary>
    public TimeSpan ActiveHeartbeatWindow => TimeSpan.FromSeconds(this.ActiveHeartbeatWindowSeconds);

    /// <summary>The fleet-empty settle period as a <see cref="TimeSpan" />.</summary>
    public TimeSpan FleetEmptySettle => TimeSpan.FromSeconds(this.FleetEmptySettleSeconds);

    /// <summary>The queue-stall grace period as a <see cref="TimeSpan" />.</summary>
    public TimeSpan QueueStallGrace => TimeSpan.FromSeconds(this.QueueStallGraceSeconds);
}

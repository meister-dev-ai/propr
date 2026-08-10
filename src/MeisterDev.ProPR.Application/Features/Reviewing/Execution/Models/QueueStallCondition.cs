// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>Why a queue with work in it is not moving.</summary>
public enum QueueStallCause
{
    /// <summary>Runners are registered, but none has been heard from inside the active window.</summary>
    NoActiveRunner = 0,

    /// <summary>
    ///     Runners are active but none is taking the pending work. Slot exhaustion is the usual cause; the
    ///     condition does not establish it, so a runner that is alive and failing to poll reports here too.
    /// </summary>
    NoFreeSlot,

    /// <summary>
    ///     Work is waiting for a tag no currently active runner declares. A runner enrolling, coming back,
    ///     or declaring the tag makes it routable again, so this is a snapshot rather than a verdict.
    /// </summary>
    NoRunnerMatchesRequiredTags,
}

/// <summary>
///     A queue that has work and is not moving.
///     <para>
///         Raised rather than inferred, because a stalled queue and an idle one look identical from
///         outside: both are simply a list of pending jobs. Naming the cause is the difference between an
///         operator seeing "nothing to do" and seeing "your fleet went offline forty minutes ago".
///     </para>
/// </summary>
/// <param name="Cause">Why nothing is moving.</param>
/// <param name="PendingJobCount">How many jobs are waiting.</param>
/// <param name="OldestPendingSince">When the longest-waiting job was submitted.</param>
/// <param name="Detail">Operator-readable detail, such as the tags nothing declares.</param>
public sealed record QueueStallCondition(
    QueueStallCause Cause,
    int PendingJobCount,
    DateTimeOffset OldestPendingSince,
    string? Detail = null);

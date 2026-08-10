// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     What the control plane tells an executing party to do when it renews its lease. The heartbeat is the
///     only channel that reaches an execution wherever it runs, which is what makes it the place to carry
///     this rather than a signal that only works inside one process.
/// </summary>
public enum ReviewJobDirective
{
    /// <summary>Keep going.</summary>
    Continue = 0,

    /// <summary>Stop working on this job. <see cref="ReviewJobLeaseRenewal.StopReason" /> says why.</summary>
    Stop = 1,
}

/// <summary>Why an executing party was told to stop.</summary>
public enum ReviewJobStopReason
{
    /// <summary>No stop was issued.</summary>
    None = 0,

    /// <summary>An administrator halted the review deliberately.</summary>
    OperatorStop = 1,

    /// <summary>A newer revision arrived, so this review is reviewing something nobody is waiting for.</summary>
    Superseded = 2,

    /// <summary>A hard budget cap was reached, so the job finalises as budget-exceeded, not as a failure.</summary>
    BudgetCapReached = 3,

    /// <summary>The executing party's registration was revoked and it may no longer hold work.</summary>
    RegistrationRevoked = 4,

    /// <summary>
    ///     The caller no longer holds the lease: someone else took the job over. Distinct from the reasons
    ///     above, which are decisions about the job itself rather than about who is running it.
    /// </summary>
    LeaseNoLongerHeld = 5,
}

/// <summary>
///     Outcome of one attempt to renew a lease, and the control-plane's answer to the party that asked.
/// </summary>
/// <param name="Accepted">
///     Whether the caller still holds the lease and its expiry was moved forward. False when the caller's
///     generation is stale, it is not the recorded owner, or the job has reached a terminal state.
/// </param>
/// <param name="ExpiresAt">The new expiry when accepted; null when rejected.</param>
/// <param name="Directive">Whether to keep working on the job.</param>
/// <param name="StopReason">Why to stop, when the directive says to.</param>
public sealed record ReviewJobLeaseRenewal(
    bool Accepted,
    DateTimeOffset? ExpiresAt,
    ReviewJobDirective Directive = ReviewJobDirective.Continue,
    ReviewJobStopReason StopReason = ReviewJobStopReason.None)
{
    /// <summary>A renewal refused because the caller no longer holds the lease.</summary>
    public static ReviewJobLeaseRenewal Rejected { get; } =
        new(false, null, ReviewJobDirective.Stop, ReviewJobStopReason.LeaseNoLongerHeld);

    /// <summary>A renewal refused because the job itself was halted, naming which decision halted it.</summary>
    public static ReviewJobLeaseRenewal StoppedBecause(ReviewJobStopReason reason)
    {
        return new ReviewJobLeaseRenewal(false, null, ReviewJobDirective.Stop, reason);
    }
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     A job whose lease has expired, with the generation observed when it was found. The generation is what
///     makes the reclaim safe: by the time the sweep gets round to this job, its original holder may have
///     recovered and renewed, and the reclaim must then do nothing.
/// </summary>
/// <param name="JobId">The job whose lease expired.</param>
/// <param name="Generation">The lease generation observed at the time of the scan.</param>
/// <param name="ExpiredAt">When the expired lease was due to be renewed.</param>
public sealed record ExpiredReviewJobLease(Guid JobId, int Generation, DateTimeOffset ExpiredAt);

/// <summary>What a reclaim attempt did.</summary>
public enum ReviewJobReclaimOutcome
{
    /// <summary>
    ///     Nothing. The job was no longer in the state the caller observed, usually because its holder
    ///     recovered and renewed, or because another host reclaimed it first.
    /// </summary>
    NotReclaimed = 0,

    /// <summary>The job was returned to the pending pool and can be picked up again.</summary>
    Requeued = 1,

    /// <summary>
    ///     The job's reclaim budget was exhausted, so it was failed rather than cycled again. Its failure
    ///     names the lease loss, so an operator can tell it apart from a review that failed on its own merits.
    /// </summary>
    FailedOutOfReclaimBudget = 2,
}

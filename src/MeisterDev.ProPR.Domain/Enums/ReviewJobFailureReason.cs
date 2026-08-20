// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Domain.Enums;

/// <summary>
///     Why a review job failed, in a form code and operators can both act on. The free-text error message
///     stays alongside it for detail, but a reader deciding what to do about a failure should not have to
///     parse prose to tell an infrastructure interruption from a review that genuinely went wrong.
/// </summary>
public enum ReviewJobFailureReason
{
    /// <summary>No categorised reason. The failure is described only by its error message.</summary>
    Unspecified = 0,

    /// <summary>
    ///     The job was interrupted often enough that its reclaim budget ran out. Each interruption on its own
    ///     is normal (a deploy, a host loss, a database outage); repeatedly failing to make progress across
    ///     them is not.
    /// </summary>
    LeaseLost = 1,

    /// <summary>
    ///     Publication began and did not finish within its own timeout. Distinct from a lease loss because
    ///     comments may already be posted, so the job is surfaced for a human rather than reclaimed.
    /// </summary>
    PublicationTimedOut = 2,

    /// <summary>
    ///     The execution ran past the ceiling on one execution and was stopped at a renewal. Distinct from a
    ///     lease loss because nothing was interrupted or reclaimed: the job held its lease throughout and was
    ///     stopped for how long it had been holding it. Comments it had already posted stay on the pull
    ///     request, as with a publication timeout.
    /// </summary>
    MaxDurationExceeded = 3,
}

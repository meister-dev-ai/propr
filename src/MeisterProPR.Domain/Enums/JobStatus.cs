// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>
///     Status of a review job.
/// </summary>
public enum JobStatus
{
    /// <summary>Job is queued and waiting to start.</summary>
    Pending,

    /// <summary>Job is currently processing.</summary>
    Processing,

    /// <summary>Job completed successfully.</summary>
    Completed,

    /// <summary>Job failed with an error.</summary>
    Failed,

    /// <summary>Job was cancelled because the pull request was abandoned before the review completed.</summary>
    Cancelled = 4,

    /// <summary>
    ///     Job was superseded because a newer push arrived for the same pull request before the review
    ///     completed. Unlike <see cref="Cancelled" /> (pull request abandoned/closed), a superseded job's
    ///     reviewed per-file results remain a valid baseline whose unchanged files can be inherited by the
    ///     newer revision's review.
    /// </summary>
    Superseded = 5,

    /// <summary>
    ///     Job was stopped manually by a client administrator through the control panel. Unlike
    ///     <see cref="Cancelled" /> (pull request abandoned) and <see cref="Superseded" /> (a newer push
    ///     arrived), this is a deliberate operator action that halts an in-flight or queued review; its
    ///     partial results are not treated as a reusable baseline.
    /// </summary>
    Stopped = 6,

    /// <summary>
    ///     Job was held before it started because a client or pull-request budget soft cap had been reached, so
    ///     no new review was admitted. A held job never ran; it is resumed manually by restarting it once budget
    ///     is freed, and it is still superseded by a newer push or cancelled when the pull request closes.
    /// </summary>
    BudgetHeld = 7,

    /// <summary>
    ///     Job was stopped in-flight because a hard budget cap was reached; the findings produced before the cap
    ///     are published. It is resumed manually by restarting it once budget is freed (already-paid files are
    ///     carried forward), and it is still superseded by a newer push or cancelled when the pull request closes.
    /// </summary>
    BudgetExceeded = 8,
}

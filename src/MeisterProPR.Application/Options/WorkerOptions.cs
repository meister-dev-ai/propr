// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.ComponentModel.DataAnnotations;

namespace MeisterProPR.Application.Options;

/// <summary>
///     Configuration options for the review job background worker.
///     Bound from environment variables; validated on application startup.
/// </summary>
public sealed class WorkerOptions
{
    /// <summary>
    ///     Milliseconds between review job polling cycles.
    ///     Bound to <c>WORKER_POLL_INTERVAL_MILLISECONDS</c>.
    /// </summary>
    [Range(10, 60000, ErrorMessage = "PollIntervalMilliseconds must be between 10 and 60000.")]
    public int PollIntervalMilliseconds { get; set; } = 2000;

    /// <summary>
    ///     Minutes before a job stuck in the <c>Processing</c> state is transitioned to <c>Failed</c>.
    ///     Bound to <c>WORKER_STUCK_JOB_TIMEOUT_MINUTES</c>.
    /// </summary>
    [Range(5, 1440, ErrorMessage = "StuckJobTimeoutMinutes must be between 5 and 1440.")]
    public int StuckJobTimeoutMinutes { get; set; } = 30;

    /// <summary>
    ///     Maximum number of review jobs the worker runs concurrently in a single cycle when parallel
    ///     review execution is licensed. Bounds the peak memory/CPU multiplier of simultaneous reviews;
    ///     jobs beyond the cap are picked up on subsequent poll cycles.
    ///     Bound to <c>WORKER_MAX_CONCURRENT_REVIEW_JOBS</c>.
    /// </summary>
    [Range(1, 64, ErrorMessage = "MaxConcurrentReviewJobs must be between 1 and 64.")]
    public int MaxConcurrentReviewJobs { get; set; } = 4;
}

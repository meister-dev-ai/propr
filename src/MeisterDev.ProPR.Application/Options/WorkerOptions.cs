// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.ComponentModel.DataAnnotations;

namespace MeisterDev.ProPR.Application.Options;

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
    ///     Retired. Jobs were once failed for sitting in the <c>Processing</c> state longer than this, which
    ///     could not tell a long review from an abandoned one and, with more than one host, let one host fail
    ///     another host's healthy review. Liveness is now the job's lease, configured through
    ///     <see cref="ReviewLeaseOptions" />.
    ///     <para>
    ///         Still read from <c>WORKER_STUCK_JOB_TIMEOUT_MINUTES</c> so an existing deployment starts
    ///         unchanged, and reported at startup so an operator learns it no longer does anything. Null when
    ///         the variable is not set.
    ///     </para>
    /// </summary>
    public int? RetiredStuckJobTimeoutMinutes { get; set; }

    /// <summary>
    ///     Maximum number of review jobs the worker runs concurrently in a single cycle when parallel
    ///     review execution is licensed. Bounds the peak memory/CPU multiplier of simultaneous reviews;
    ///     jobs beyond the cap are picked up on subsequent poll cycles.
    ///     Bound to <c>WORKER_MAX_CONCURRENT_REVIEW_JOBS</c>.
    /// </summary>
    [Range(1, 64, ErrorMessage = "MaxConcurrentReviewJobs must be between 1 and 64.")]
    public int MaxConcurrentReviewJobs { get; set; } = 4;
}

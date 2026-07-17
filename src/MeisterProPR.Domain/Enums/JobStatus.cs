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
}

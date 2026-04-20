// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Crawling.Execution.Models;

/// <summary>High-level review decision produced by shared pull-request synchronization.</summary>
public enum PullRequestSynchronizationReviewDecision
{
    /// <summary>No review-intake decision was required.</summary>
    None = 0,

    /// <summary>A new review job was queued.</summary>
    Submitted = 1,

    /// <summary>An active job already existed for the current iteration.</summary>
    DuplicateActiveJob = 2,

    /// <summary>No new reviewable changes were detected.</summary>
    NoReviewChanges = 3,

    /// <summary>The configured ProCursor source scope was invalid.</summary>
    InvalidSourceScope = 4,

    /// <summary>The configured ProCursor source scope was empty.</summary>
    EmptySourceScope = 5,
}

/// <summary>High-level lifecycle decision produced by shared pull-request synchronization.</summary>
public enum PullRequestSynchronizationLifecycleDecision
{
    /// <summary>No lifecycle action was required.</summary>
    None = 0,

    /// <summary>One or more active review jobs were cancelled.</summary>
    CancelledActiveJobs = 1,

    /// <summary>The pull request was closed but no active jobs needed cancellation.</summary>
    NoActiveJobsToCancel = 2,
}

/// <summary>Shared downstream synchronization result used by callers and delivery-history logging.</summary>
/// <param name="ReviewDecision">The review-intake decision.</param>
/// <param name="LifecycleDecision">The lifecycle decision.</param>
/// <param name="ActionSummaries">Operator-visible summaries describing what synchronization did.</param>
public sealed record PullRequestSynchronizationOutcome(
    PullRequestSynchronizationReviewDecision ReviewDecision,
    PullRequestSynchronizationLifecycleDecision LifecycleDecision,
    IReadOnlyList<string> ActionSummaries);

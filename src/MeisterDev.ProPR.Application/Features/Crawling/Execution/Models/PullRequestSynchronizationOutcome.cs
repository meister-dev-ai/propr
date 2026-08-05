// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;

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

    /// <summary>
    ///     A prior review job for the same revision already failed and the pull request has not been updated.
    ///     Automatic re-review is suppressed to avoid looping on a deterministic failure; the user must restart it manually.
    /// </summary>
    FailedAwaitingRestart = 6,

    /// <summary>
    ///     The client already has a review at an earlier revision of the pull request and reviews only the first
    ///     increment, so this automatic trigger created nothing. Any job still running at the earlier revision
    ///     keeps running.
    /// </summary>
    SubsequentIncrementSkipped = 7,
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

/// <summary>High-level thread-pass decision produced by shared pull-request synchronization.</summary>
/// <remarks>
///     The conversation and the file review are separate units of work on separate cadences, so their
///     decisions and their job identities never share a field. A caller that followed one and found the
///     other's id would be watching work nobody did.
/// </remarks>
public enum PullRequestSynchronizationThreadPassDecision
{
    /// <summary>No thread-pass decision was reached, because the pass never got as far as the trigger.</summary>
    None = 0,

    /// <summary>A thread pass was queued.</summary>
    Queued = 1,

    /// <summary>Neither the revision nor any reviewer-owned thread's comment count moved.</summary>
    NotDue = 2,

    /// <summary>The client has automatic comment resolution switched off.</summary>
    ResolutionDisabled = 3,

    /// <summary>The provider does not advertise the thread-status capability.</summary>
    ProviderUnsupported = 4,

    /// <summary>A thread pass already holds this pull request, or already ran for this trigger state.</summary>
    AlreadyClaimed = 5,

    /// <summary>
    ///     Deciding whether the conversation was due threw, so nothing is known about it. Distinct from
    ///     <see cref="None" />, which says the trigger was never reachable: a failure that reports itself as
    ///     "nothing to do" is a pull request whose threads quietly stop being answered.
    /// </summary>
    Failed = 6,
}

/// <summary>Shared downstream synchronization result used by callers and delivery-history logging.</summary>
/// <param name="ReviewDecision">The file pass's review-intake decision.</param>
/// <param name="LifecycleDecision">The lifecycle decision.</param>
/// <param name="ActionSummaries">Operator-visible summaries describing what synchronization did.</param>
/// <param name="JobId">
///     The review job this pass settled on: the one it queued, or the active one it declined to duplicate.
///     A caller that asked for the review has nothing to follow the work with unless it comes back. Null
///     whenever the pass reached no job at all.
/// </param>
/// <param name="ThreadPassDecision">The thread pass's own decision, which the file pass's does not speak for.</param>
/// <param name="ThreadPassJobId">The thread pass this synchronization settled on, when it reached one.</param>
public sealed record PullRequestSynchronizationOutcome(
    PullRequestSynchronizationReviewDecision ReviewDecision,
    PullRequestSynchronizationLifecycleDecision LifecycleDecision,
    IReadOnlyList<string> ActionSummaries,
    Guid? JobId = null,
    PullRequestSynchronizationThreadPassDecision ThreadPassDecision =
        PullRequestSynchronizationThreadPassDecision.None,
    Guid? ThreadPassJobId = null);

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Reviewing.Intake.Commands.SubmitReviewByCoordinates;

/// <summary>Why a coordinate-addressed review request did or did not produce a review job.</summary>
/// <remarks>
///     Every outcome is named rather than reduced to a status code, because the caller shows the reason to a
///     person: "the pull request is closed" and "we cannot reach your provider" both refuse the request, and
///     they ask for entirely different next steps.
/// </remarks>
public enum SubmitReviewByCoordinatesOutcome
{
    /// <summary>A review job was queued for the revision the provider reported.</summary>
    Submitted = 0,

    /// <summary>A review is already running at this exact revision, and its identifier is returned instead.</summary>
    DuplicateActiveJob = 1,

    /// <summary>
    ///     No configuration of this client covers the coordinates. That match is the authorization boundary,
    ///     so failing it is refused the same way a missing role is, and for the same reason.
    /// </summary>
    NotAuthorized = 2,

    /// <summary>The provider reports no such pull request under these coordinates.</summary>
    PullRequestNotFound = 3,

    /// <summary>
    ///     The provider could not be asked, or answered without a revision. Without commit identity there is
    ///     nothing to review, and retrying later may well succeed.
    /// </summary>
    RevisionUnresolvable = 4,

    /// <summary>
    ///     The pull request exists but is not in a state ProPR will review: closed, merged, blocked from
    ///     processing, or configured with a code-knowledge source scope that no longer resolves.
    /// </summary>
    NotSubmittable = 5,

    /// <summary>
    ///     The pull request and its revision resolved, but queueing the review failed inside ProPR. This is
    ///     a fault in the deployment rather than anything about the request, and it is named separately from
    ///     an unresolvable revision so nobody is sent to check a source-control connection that answered
    ///     perfectly well.
    /// </summary>
    SubmissionFailed = 6,
}

/// <summary>The answer to a coordinate-addressed review request.</summary>
/// <param name="Outcome">The named outcome.</param>
/// <param name="JobId">
///     The review job to follow: the one queued, or the active one that already covers this revision.
/// </param>
/// <param name="Reason">A sentence explaining a refusal, safe to show to the person who asked.</param>
public sealed record SubmitReviewByCoordinatesResult(
    SubmitReviewByCoordinatesOutcome Outcome,
    Guid? JobId = null,
    string? Reason = null);

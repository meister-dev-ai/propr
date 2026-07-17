// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Support;

/// <summary>
///     Provider-neutral selection and classification of a prior review job to reuse as a cross-revision
///     carry-forward baseline. Kept independent of any specific SCM provider so all providers share one
///     definition of "which prior job carries forward" and "did it review everything at its revision".
/// </summary>
public static class ReviewBaselineSelection
{
    /// <summary>
    ///     Counts the per-file results that represent freshly reviewed, reusable work — excluding failed,
    ///     excluded, and carried-forward rows. This is the "usable reviewed" count used both to rank
    ///     baselines and to decide whether a terminal job covered its full revision.
    /// </summary>
    public static int CountUsableReviewedResults(ReviewJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return job.FileReviewResults.Count(result =>
            result.IsComplete && !result.IsFailed && !result.IsExcluded && !result.IsCarriedForward);
    }

    /// <summary>
    ///     Determines whether a terminal baseline reviewed every in-scope file at its own revision. A
    ///     completed job always qualifies; a job that ended in another terminal state qualifies only when its
    ///     usable reviewed count reached the in-scope changed-file count fixed at its dispatch planning.
    ///     Partial baselines (this returns <see langword="false" />) must be treated conservatively: the
    ///     current review has to fetch the full pull request so never-reviewed files are not skipped.
    /// </summary>
    public static bool IsFullCoverage(ReviewJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Status == JobStatus.Completed)
        {
            return true;
        }

        return job.InScopeChangedFileCount is > 0
               && CountUsableReviewedResults(job) >= job.InScopeChangedFileCount;
    }

    /// <summary>
    ///     Selects the most-recent terminal job usable as a carry-forward baseline from <paramref name="candidates" />:
    ///     a job whose stored revision key differs from <paramref name="currentRevisionKey" />, ranked by usable
    ///     reviewed-result count (descending) and recency, with an abandoned-pull-request cancellation deprioritized
    ///     below every other terminal state and completed jobs preferred over other terminal states on ties.
    /// </summary>
    public static ReviewJob? SelectReusableBaseline(IEnumerable<ReviewJob> candidates, string currentRevisionKey)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates
            .Where(job => !string.Equals(
                ReviewRevisionKeys.GetStoredKey(job.ReviewRevisionReference, job.IterationId),
                currentRevisionKey,
                StringComparison.Ordinal))
            .OrderByDescending(job => job.Status == JobStatus.Cancelled ? 0 : 1)
            .ThenByDescending(CountUsableReviewedResults)
            .ThenByDescending(job => StatusPreferenceRank(job.Status))
            .ThenByDescending(job => job.CompletedAt)
            .FirstOrDefault();
    }

    private static int StatusPreferenceRank(JobStatus status)
    {
        return status switch
        {
            JobStatus.Completed => 3,
            JobStatus.Superseded => 2,
            JobStatus.Failed => 1,
            _ => 0,
        };
    }
}

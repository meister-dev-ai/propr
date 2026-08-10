// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Support;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Application.Services;

/// <summary>
///     What a new review adopts from the reviews before it: a prior attempt's finished files at the same
///     revision (resume), and a prior iteration's results for files that have not changed since
///     (carry-forward).
///     <para>
///         One implementation for both execution paths. The in-process orchestration applies this at
///         review start; the dispatch preparer applies it before a job's manifest leaves for a runner, so
///         the executor's prior-results read returns the adopted rows and a resumed or superseding remote
///         review neither re-pays work nor synthesizes over a different set than the local path would.
///         Splitting the logic per path allows the two to diverge. A remote review that re-reviews every
///         file produces a different result and a different cost.
///     </para>
/// </summary>
public sealed partial class ReviewJobReuse(
    IReviewJobExecutionStore jobs,
    IReviewPrScanWatermarkStore scans,
    ILogger logger)
{
    /// <summary>
    ///     Resolves what this job may adopt: the resume candidate at its own revision, and the
    ///     carry-forward baseline from the iteration before it, with the compare handle the in-process
    ///     fetch delta-scopes against.
    /// </summary>
    /// <param name="job">The job about to be reviewed or dispatched.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task<ReviewJobReuseState> LoadScanStateAsync(ReviewJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);

        var scan = await scans.GetAsync(job.ClientId, job.RepositoryId, job.PullRequestId, ct);
        var iterationKey = ReviewRevisionKeys.GetStoredKey(job.ReviewRevisionReference, job.IterationId);
        var isNewIteration = scan is null || scan.LastProcessedCommitId != iterationKey;

        // Resume exists so work already done at this revision is not redone after an interruption, and it
        // adopts a prior job's finished files wholesale. On an explicitly requested review of a revision
        // already reviewed, that prior job is the completed review itself, so every file would be adopted
        // and none re-reviewed: the request would report success having reviewed nothing. Redoing the work
        // is the whole point of asking, so resume stands down for that case.
        //
        // A run stopped by a budget cap is the exception. It records the revision as processed even though it
        // reviewed only part of it, so the revision looks reviewed and resume would stand down, leaving the
        // only way to finish the job a full re-review of everything it already paid for. Continuing from where
        // it stopped is precisely what asking again means there.
        var resumeCandidate = await this.FindResumeJobIfAnyAsync(job, ct);
        var resumeJob = isNewIteration
                        || !job.AllowUnchangedResubmission
                        || StoppedShortAtBudgetCap(resumeCandidate)
            ? resumeCandidate
            : null;

        var (baselineJob, baselineIsFullCoverage, compareToIterationId, compareToReviewRevision) =
            await this.ResolveCarryForwardBaselineAsync(job, isNewIteration, iterationKey, ct);

        return new ReviewJobReuseState(
            isNewIteration,
            baselineJob,
            baselineIsFullCoverage,
            resumeJob,
            compareToIterationId,
            compareToReviewRevision);
    }

    /// <summary>
    ///     Adopts the finished per-file results of a prior attempt at this same revision, and reports how
    ///     many were taken. Every adopted file is one this review does not pay an AI call for again, so the
    ///     count is worth stating rather than leaving to be inferred from an absence of work.
    /// </summary>
    public async Task<int> ResumePriorFileResultsAsync(
        ReviewJob job,
        ReviewJob? resumeJob,
        HashSet<string> changedPathsSet,
        HashSet<string> claimedPaths,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(changedPathsSet);
        ArgumentNullException.ThrowIfNull(claimedPaths);

        if (resumeJob is null)
        {
            return 0;
        }

        await this.ClaimPathsAlreadyOnJobAsync(job, claimedPaths, ct);

        var inherited = 0;
        foreach (var priorResult in resumeJob.FileReviewResults
                     .Where(fr => fr.IsComplete && !fr.IsFailed && !fr.IsExcluded && !fr.IsCarriedForward))
        {
            if (!changedPathsSet.Contains(priorResult.FilePath))
            {
                continue;
            }

            if (!claimedPaths.Add(priorResult.FilePath))
            {
                continue;
            }

            var resumed = ReviewFileResult.CreateResumed(job.Id, priorResult);
            await jobs.AddFileResultAsync(resumed, ct);
            inherited++;
        }

        if (inherited > 0)
        {
            LogResumedPriorFileResults(logger, job.Id, inherited, resumeJob.Id, resumeJob.Status);
        }

        return inherited;
    }

    // Carries forward reviewed file results from a prior baseline job at a different revision.
    //
    // Full-coverage baseline: the current scope is delta-scoped against it, so a reviewed file that is NOT
    // in the delta (<paramref name="changedPathsSet" /> holds the delta) is provably unchanged and carries
    // forward. This is the long-standing behaviour and is unchanged for a completed baseline.
    //
    // Partial baseline (cancelled/failed/superseded mid per-file review): the current scope is the full PR
    // (<paramref name="changedPathsSet" /> holds every current file), so carry forward every reviewed file
    // still present as an AI-skip set and let the dispatcher review the rest fresh. This keeps files that
    // are unchanged since the baseline, but were never reviewed by it, from being skipped.
    public async Task<List<string>> CarryForwardBaselineResultsAsync(
        ReviewJob job,
        ReviewJob? baselineJob,
        bool baselineIsFullCoverage,
        HashSet<string> changedPathsSet,
        ReviewExclusionRules exclusionRules,
        HashSet<string> claimedPaths,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(changedPathsSet);
        ArgumentNullException.ThrowIfNull(exclusionRules);
        ArgumentNullException.ThrowIfNull(claimedPaths);

        var carriedForwardPaths = new List<string>();
        if (baselineJob is null)
        {
            return carriedForwardPaths;
        }

        await this.ClaimPathsAlreadyOnJobAsync(job, claimedPaths, ct);

        foreach (var priorResult in baselineJob.FileReviewResults
                     .Where(fr => fr.IsComplete && !fr.IsFailed && !fr.IsExcluded && !fr.IsCarriedForward))
        {
            var shouldCarryForward = baselineIsFullCoverage
                ? !changedPathsSet.Contains(priorResult.FilePath)
                : changedPathsSet.Contains(priorResult.FilePath);
            if (!shouldCarryForward)
            {
                continue;
            }

            // Exclusion drift only applies on the partial (full-fetch) path: there the skipped file still
            // reaches the dispatcher to be recorded as excluded. On the delta path the file is absent from
            // the fetch, so skipping carry-forward would drop it entirely. Carry-forward is preserved.
            if (!baselineIsFullCoverage && exclusionRules.Matches(priorResult.FilePath))
            {
                continue;
            }

            if (!claimedPaths.Add(priorResult.FilePath))
            {
                continue;
            }

            var carried = ReviewFileResult.CreateCarriedForward(job.Id, priorResult);
            await jobs.AddFileResultAsync(carried, ct);
            carriedForwardPaths.Add(priorResult.FilePath);
        }

        return carriedForwardPaths;
    }

    /// <summary>
    ///     Claims the paths this job already has rows for, making adoption idempotent. A job can reach
    ///     adoption with rows already written, for example dispatched to a runner, adopted there, then
    ///     refused and later run in-process. Adding a second row for the same file violates the one-row-per-file
    ///     index and fails the review outright.
    /// </summary>
    private async Task ClaimPathsAlreadyOnJobAsync(ReviewJob job, HashSet<string> claimedPaths, CancellationToken ct)
    {
        var current = await jobs.GetByIdWithFileResultsAsync(job.Id, ct);
        foreach (var row in current?.FileReviewResults ?? [])
        {
            claimedPaths.Add(row.FilePath);
        }
    }

    private async Task<ReviewJob?> FindResumeJobIfAnyAsync(ReviewJob job, CancellationToken ct)
    {
        var currentRevisionKey = ReviewRevisionKeys.TryGetStoredKey(job.ReviewRevisionReference);
        if (string.IsNullOrWhiteSpace(currentRevisionKey))
        {
            return null;
        }

        var resumeJob = await jobs.GetBestTerminalJobWithFileResultsByStoredRevisionAsync(
            job.OrganizationUrl,
            job.ProjectId,
            job.RepositoryId,
            job.PullRequestId,
            currentRevisionKey,
            ct);

        // A resume job that points at this very job is not a real resume candidate.
        return resumeJob?.Id == job.Id ? null : resumeJob;
    }

    private async Task<(
            ReviewJob? BaselineJob,
            bool BaselineIsFullCoverage,
            int? CompareToIterationId,
            ReviewRevision? CompareToReviewRevision)>
        ResolveCarryForwardBaselineAsync(
            ReviewJob job,
            bool isNewIteration,
            string iterationKey,
            CancellationToken ct)
    {
        if (!isNewIteration)
        {
            return (null, false, null, null);
        }

        // Select the carry-forward baseline from job history, using the most-recent terminal job at a
        // different revision, rather than from the scan. This lets a prior review that was
        // cancelled/failed/superseded mid-flight still seed the next review's unchanged files.
        var baselineJob = await jobs.GetLatestReusableTerminalJobAsync(
            job.OrganizationUrl,
            job.ProjectId,
            job.RepositoryId,
            job.PullRequestId,
            job.Id,
            iterationKey,
            ct);

        if (baselineJob is null)
        {
            return (null, false, null, null);
        }

        var baselineIsFullCoverage = ReviewBaselineSelection.IsFullCoverage(baselineJob);
        if (!baselineIsFullCoverage)
        {
            return (baselineJob, false, null, null);
        }

        // Full-coverage baseline: delta-scope against it so only files changed since the
        // baseline are re-reviewed. The compare handle is provider-neutral. Azure DevOps
        // reads the iteration id off the baseline job, other providers read its review revision.
        if (job.Provider == ScmProvider.AzureDevOps)
        {
            var baselineIterationId = ResolveBaselineIterationId(baselineJob);
            if (baselineIterationId is > 0 && baselineIterationId < job.IterationId)
            {
                return (baselineJob, true, baselineIterationId, null);
            }

            // Out-of-order or unavailable iteration id: fall back to a full fetch and treat
            // the baseline purely as an AI-skip set rather than risk a negative delta.
            return (baselineJob, false, null, null);
        }

        return (baselineJob, true, null, baselineJob.ReviewRevisionReference);
    }

    /// <summary>
    ///     Whether a prior run at this revision was stopped by a budget cap before it had reviewed everything
    ///     in scope.
    /// </summary>
    /// <remarks>
    ///     Both halves are needed. The budget block alone does not mean work is outstanding: a cap tripped on
    ///     the last file leaves nothing to continue, and adopting that job wholesale would answer a re-review
    ///     request with no fresh work at all. The coverage comparison is what distinguishes a run that stopped
    ///     short from one that merely finished expensively.
    /// </remarks>
    private static bool StoppedShortAtBudgetCap(ReviewJob? candidate)
    {
        return candidate is not null
               && candidate.BudgetBlockScope is not null
               && candidate.InScopeChangedFileCount is > 0
               && ReviewBaselineSelection.CountUsableReviewedResults(candidate) < candidate.InScopeChangedFileCount;
    }

    // Derives the Azure DevOps iteration id to compare against from the baseline job itself: prefer the
    // iteration id carried in its review revision (ProviderRevisionId), falling back to the stored iteration.
    private static int? ResolveBaselineIterationId(ReviewJob baselineJob)
    {
        var iterationFromRevision = ReviewRevisionKeys.TryParseIterationId(ReviewRevisionKeys.TryGetStoredKey(baselineJob.ReviewRevisionReference));
        return iterationFromRevision ?? (baselineJob.IterationId > 0 ? baselineJob.IterationId : null);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message =
            "Review job {JobId} resumed {InheritedFileCount} completed file results from prior attempt "
            + "{ResumeJobId} ({ResumeJobStatus}) at the same revision; those files are not reviewed again")]
    private static partial void LogResumedPriorFileResults(
        ILogger logger,
        Guid jobId,
        int inheritedFileCount,
        Guid resumeJobId,
        JobStatus resumeJobStatus);
}

/// <summary>What a job may adopt, and the compare handle a delta-scoped fetch uses.</summary>
/// <param name="IsNewIteration">Whether this revision differs from the last one the scan recorded.</param>
/// <param name="BaselineJob">The carry-forward baseline from an earlier iteration, when one exists.</param>
/// <param name="BaselineIsFullCoverage">Whether the baseline reviewed everything it was asked to.</param>
/// <param name="ResumeJob">The prior attempt at this same revision whose finished files may be adopted.</param>
/// <param name="CompareToIterationId">The Azure DevOps iteration a delta fetch compares against.</param>
/// <param name="CompareToReviewRevision">The revision other providers' delta fetch compares against.</param>
public sealed record ReviewJobReuseState(
    bool IsNewIteration,
    ReviewJob? BaselineJob,
    bool BaselineIsFullCoverage,
    ReviewJob? ResumeJob,
    int? CompareToIterationId,
    ReviewRevision? CompareToReviewRevision);

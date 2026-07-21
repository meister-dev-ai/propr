// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterProPR.Application.Features.Budgeting;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Services;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;

/// <summary>
///     Plans and dispatches per-file review work for a PR. This planner owns retry-aware file-result reuse,
///     exclusion handling, deterministic file ordering, concurrency limiting, and aggregation of per-file review
///     failures so the orchestrator can reason in terms of successful dispatch vs. partial failure.
/// </summary>
internal sealed class FileReviewDispatchPlanner(
    IJobRepository jobRepository,
    IProtocolRecorder protocolRecorder,
    FileReviewer fileReviewer,
    AiReviewOptions options,
    ILogger<FileByFileReviewOrchestrator> logger,
    IBudgetScopeAccessor? budgetScopeAccessor = null)
{
    public async Task<FileReviewDispatchResult> ExecuteAsync(
        ReviewJob job,
        PullRequest pr,
        ReviewSystemContext baseContext,
        IChatClient effectiveClient,
        CancellationToken ct)
    {
        var executionContext = baseContext;
        var jobWithResults = await jobRepository.GetByIdWithFileResultsAsync(job.Id, ct) ?? job;

        var existingResults = jobWithResults.FileReviewResults.ToDictionary(r => r.FilePath);

        // Some SCM providers (notably Azure DevOps) can return the same path more than once for a
        // single iteration when a file was touched in multiple commits within the iteration, after
        // a force-push that creates a new iteration over the same set of files, or for renamed
        // files that share both old and new paths. Treat the changed-files manifest as a set so the
        // file-index dictionary and downstream lookups do not throw on duplicate keys (which would
        // make the review retry loop unrecoverable).
        var allChangedFiles = DedupeChangedFiles(pr.ChangedFiles, job.Id, logger);

        var selection = ReviewFileSelectionService.SelectFilesForReview(allChangedFiles, existingResults, executionContext.ExclusionRules);
        var filesToReview = selection.FilesToReview.ToList();

        foreach (var excludedFile in selection.ExcludedFiles)
        {
            var existingExcluded = existingResults.GetValueOrDefault(excludedFile.Path);
            await this.MarkFileExcludedAsync(job, excludedFile, executionContext, existingExcluded, ct);
        }

        // Fix the "files reviewed" progress denominator: deduped in-scope changed files after exclusions
        // for this iteration. Derived from the frozen changed set and the exclusion rules — NOT from
        // selection.ExcludedFiles, which drops already-complete excluded rows on a retry and would inflate
        // the denominator. Computing it from the rules keeps it stable across re-dispatch. Persisted before
        // the zero-files early return below so the denominator exists even when nothing needs review.
        var exclusionRules = executionContext.ExclusionRules;
        var inScopeChangedFileCount = allChangedFiles.Count(f => !exclusionRules.Matches(f.Path));
        await jobRepository.UpdateInScopeChangedFileCountAsync(job.Id, inScopeChangedFileCount, ct);

        filesToReview =
        [
            .. filesToReview
                .OrderBy(f => f.IsBinary || f.ChangeType == ChangeType.Delete ? 1 : 0)
                .ThenByDescending(f => (f.FullContent?.Length ?? 0) + (f.UnifiedDiff?.Length ?? 0))
                .ThenBy(f => f.Path),
        ];

        var exceptions = new List<Exception>();
        var budgetSoftCapSkippedFiles = new List<string>();
        if (filesToReview.Count == 0)
        {
            return new FileReviewDispatchResult(existingResults, exceptions, BudgetSoftCapSummary.None);
        }

        var semaphore = new SemaphoreSlim(options.MaxFileReviewConcurrency);
        var fileIndexByPath = allChangedFiles
            .Select((f, i) => (f.Path, Index: i + 1))
            .ToDictionary(x => x.Path, x => x.Index);
        var totalChangedFileCount = allChangedFiles.Count;

        async Task ReviewFileAsync(ChangedFile file)
        {
            await semaphore.WaitAsync(ct);
            try
            {
                // Per-increment soft cap: once this job's spend has reached it, stop starting new files. Files
                // already in flight finish; queued files skip here so the review concludes with a synthesis over
                // what was reviewed rather than being cut mid-call. Never applies to a job that has not spent yet.
                var budgetScope = budgetScopeAccessor?.Current;
                if (budgetScope is not null && budgetScope.IsIncrementSoftCapReached())
                {
                    lock (budgetSoftCapSkippedFiles)
                    {
                        budgetSoftCapSkippedFiles.Add(file.Path);
                    }

                    logger.LogInformation(
                        "Skipped file {FilePath} in job {JobId}: per-increment budget soft cap reached",
                        file.Path,
                        job.Id);
                    return;
                }

                var fileIndex = fileIndexByPath.GetValueOrDefault(file.Path, 1);
                var existingResult = existingResults.GetValueOrDefault(file.Path);
                await fileReviewer.ReviewAsync(
                    job,
                    pr,
                    file,
                    fileIndex,
                    totalChangedFileCount,
                    executionContext,
                    existingResult,
                    effectiveClient,
                    ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed review for file {FilePath} in job {JobId}", file.Path, job.Id);
                lock (exceptions)
                {
                    exceptions.Add(ex);
                }
            }
            finally
            {
                semaphore.Release();
            }
        }

        var tasks = new List<Task>(filesToReview.Count);
        foreach (var file in filesToReview)
        {
            tasks.Add(ReviewFileAsync(file));
        }

        try
        {
            await Task.WhenAll(tasks);
            return new FileReviewDispatchResult(existingResults, exceptions, BuildBudgetSoftCapSummary(budgetSoftCapSkippedFiles));
        }
        finally
        {
            semaphore.Dispose();
        }
    }

    // Maps the ambient scope's recorded increment soft-cap breach (if any) plus the files this run consequently
    // skipped into a provider-neutral summary. Threshold/spend are read from the breach recorded when the cap was
    // first observed; the skipped set is non-empty exactly when the cap tripped.
    private BudgetSoftCapSummary BuildBudgetSoftCapSummary(IReadOnlyList<string> skippedFiles)
    {
        var breach = budgetScopeAccessor?.Current?.IncrementSoftCapBreach;
        if (breach is null)
        {
            return BudgetSoftCapSummary.None;
        }

        return new BudgetSoftCapSummary(true, breach.ThresholdUsd, breach.SpentUsd, skippedFiles);
    }

    private async Task MarkFileExcludedAsync(
        ReviewJob job,
        ChangedFile file,
        ReviewSystemContext context,
        ReviewFileResult? existingResult,
        CancellationToken ct)
    {
        var exclusionReason = context.ExclusionRules.GetMatchingPattern(file.Path) ?? "excluded";
        logger.LogInformation(
            "Excluded file {FilePath} due to rule {Pattern} in job {JobId}",
            file.Path,
            exclusionReason,
            job.Id);

        ReviewFileResult fileResult;
        if (existingResult is { IsComplete: false })
        {
            existingResult.ResetForRetry();
            existingResult.MarkExcluded(exclusionReason);
            await jobRepository.UpdateFileResultAsync(existingResult, ct);
            fileResult = existingResult;
        }
        else
        {
            fileResult = new ReviewFileResult(job.Id, file.Path);
            fileResult.MarkExcluded(exclusionReason);
            await jobRepository.AddFileResultAsync(fileResult, ct);
        }

        Guid? protocolId = null;
        try
        {
            protocolId = await protocolRecorder.BeginAsync(
                job.Id,
                job.RetryCount + 1,
                file.Path,
                fileResult.Id,
                ct: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to begin protocol for file {FilePath} in job {JobId}", file.Path, job.Id);
        }

        if (protocolId.HasValue)
        {
            await protocolRecorder.SetCompletedAsync(protocolId.Value, "Excluded", 0, 0, 0, 0, null, ct);
        }
    }

    private static IReadOnlyList<ChangedFile> DedupeChangedFiles(
        IReadOnlyList<ChangedFile> changedFiles,
        Guid jobId,
        ILogger logger)
    {
        if (changedFiles.Count < 2)
        {
            return changedFiles;
        }

        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = 0;
        var deduped = new List<ChangedFile>(changedFiles.Count);
        foreach (var file in changedFiles)
        {
            if (seenPaths.Add(file.Path))
            {
                deduped.Add(file);
            }
            else
            {
                duplicates++;
            }
        }

        if (duplicates > 0)
        {
            logger.LogWarning(
                "Dropped {DuplicateCount} duplicate changed-file path(s) from PR manifest for job {JobId}; review will continue with the first occurrence of each path",
                duplicates,
                jobId);
        }

        return deduped;
    }

    internal sealed record FileReviewDispatchResult(
        IReadOnlyDictionary<string, ReviewFileResult> ExistingResults,
        IReadOnlyList<Exception> Exceptions,
        BudgetSoftCapSummary BudgetSoftCap);

    /// <summary>
    ///     Summary of a per-increment budget soft-cap stop within a review run: whether it tripped, the USD
    ///     threshold reached and the metered spend at that point, and the file paths that were consequently not
    ///     scanned. Provider-neutral so it can flow into the review summary without leaking budget types downstream.
    /// </summary>
    internal sealed record BudgetSoftCapSummary(
        bool SoftCapped,
        decimal? ThresholdUsd,
        decimal? SpentUsd,
        IReadOnlyList<string> SkippedFilePaths)
    {
        public static BudgetSoftCapSummary None { get; } = new(false, null, null, []);
    }
}

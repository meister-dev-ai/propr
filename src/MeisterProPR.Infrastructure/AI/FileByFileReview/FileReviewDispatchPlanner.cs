// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.AI.FileByFileReview;

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
    ILogger<FileByFileReviewOrchestrator> logger)
{
    public async Task<FileReviewDispatchResult> ExecuteAsync(
        ReviewJob job,
        PullRequest pr,
        ReviewSystemContext baseContext,
        IChatClient effectiveClient,
        CancellationToken ct)
    {
        var jobWithResults = await jobRepository.GetByIdWithFileResultsAsync(job.Id, ct) ?? job;

        var existingResults = jobWithResults.FileReviewResults.ToDictionary(r => r.FilePath);
        var completedFiles = existingResults
            .Where(kvp => kvp.Value.IsComplete)
            .Select(kvp => kvp.Key)
            .ToHashSet();

        var filesToReview = pr.ChangedFiles.Where(f => !completedFiles.Contains(f.Path)).ToList();

        if (baseContext.ExclusionRules.HasPatterns)
        {
            var excluded = filesToReview.Where(f => baseContext.ExclusionRules.Matches(f.Path)).ToList();
            if (excluded.Count > 0)
            {
                foreach (var excludedFile in excluded)
                {
                    var existingExcluded = existingResults.GetValueOrDefault(excludedFile.Path);
                    await this.MarkFileExcludedAsync(job, excludedFile, baseContext, existingExcluded, ct);
                }

                filesToReview = filesToReview.Except(excluded).ToList();
            }
        }

        filesToReview =
        [
            .. filesToReview
                .OrderBy(f => f.IsBinary || f.ChangeType == ChangeType.Delete ? 1 : 0)
                .ThenByDescending(f => (f.FullContent?.Length ?? 0) + (f.UnifiedDiff?.Length ?? 0))
                .ThenBy(f => f.Path),
        ];

        var exceptions = new List<Exception>();
        if (filesToReview.Count == 0)
        {
            return new FileReviewDispatchResult(existingResults, exceptions);
        }

        using var semaphore = new SemaphoreSlim(options.MaxFileReviewConcurrency);
        var allChangedFiles = pr.ChangedFiles.ToList();
        var fileIndexByPath = allChangedFiles
            .Select((f, i) => (f.Path, Index: i + 1))
            .ToDictionary(x => x.Path, x => x.Index);

        var tasks = filesToReview.Select(async file =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var fileIndex = fileIndexByPath.GetValueOrDefault(file.Path, 1);
                var existingResult = existingResults.GetValueOrDefault(file.Path);
                await fileReviewer.ReviewAsync(
                    job,
                    pr,
                    file,
                    fileIndex,
                    allChangedFiles.Count,
                    baseContext,
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
        });

        await Task.WhenAll(tasks);
        return new FileReviewDispatchResult(existingResults, exceptions);
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

    internal sealed record FileReviewDispatchResult(
        IReadOnlyDictionary<string, ReviewFileResult> ExistingResults,
        IReadOnlyList<Exception> Exceptions);
}

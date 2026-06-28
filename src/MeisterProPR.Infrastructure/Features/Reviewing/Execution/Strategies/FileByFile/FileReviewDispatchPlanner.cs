// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

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
    ILogger<FileByFileReviewOrchestrator> logger)
{
    public async Task<FileReviewDispatchResult> ExecuteAsync(
        ReviewJob job,
        PullRequest pr,
        ReviewSystemContext baseContext,
        IChatClient effectiveClient,
        CancellationToken ct)
    {
        var executionContext = CreateExecutionContext(baseContext);
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

        var semaphore = new SemaphoreSlim(options.MaxFileReviewConcurrency);
        var fileIndexByPath = allChangedFiles
            .Select((f, i) => (f.Path, Index: i + 1))
            .ToDictionary(x => x.Path, x => x.Index);

        var tasks = new List<Task>(filesToReview.Count);
        foreach (var file in filesToReview)
        {
            tasks.Add(
                this.ReviewFileAsync(
                    file,
                    semaphore,
                    job,
                    pr,
                    executionContext,
                    effectiveClient,
                    allChangedFiles.Count,
                    fileIndexByPath,
                    existingResults,
                    exceptions,
                    ct));
        }

        try
        {
            await Task.WhenAll(tasks);
            return new FileReviewDispatchResult(existingResults, exceptions);
        }
        finally
        {
            semaphore.Dispose();
        }
    }

    private static ReviewSystemContext CreateExecutionContext(ReviewSystemContext baseContext)
    {
        if (baseContext.AugmentationMode != ReviewAugmentationMode.LateAugmentation)
        {
            return baseContext;
        }

        return new ReviewSystemContext(baseContext.ClientSystemMessage, baseContext.RepositoryInstructions, baseContext.ReviewTools)
        {
            LoopMetrics = baseContext.LoopMetrics,
            ActiveProtocolId = baseContext.ActiveProtocolId,
            ProtocolRecorder = baseContext.ProtocolRecorder,
            ExclusionRules = baseContext.ExclusionRules,
            DismissedPatterns = baseContext.DismissedPatterns,
            PromptOverrides = baseContext.PromptOverrides,
            TierChatClient = baseContext.TierChatClient,
            ModelId = baseContext.ModelId,
            DefaultReviewChatClient = baseContext.DefaultReviewChatClient,
            DefaultReviewModelId = baseContext.DefaultReviewModelId,
            RuntimeCapabilities = baseContext.RuntimeCapabilities,
            Temperature = baseContext.Temperature,
            EnableProRV = false,
            EnableEvidenceBackedVerification = baseContext.EnableEvidenceBackedVerification,
            AugmentationMode = baseContext.AugmentationMode,
            PassKind = baseContext.PassKind,
            PerFileHint = baseContext.PerFileHint,
            PromptExperiment = baseContext.PromptExperiment,
            SkippedSteps = baseContext.SkippedSteps,
        };
    }

    private async Task ReviewFileAsync(
        ChangedFile file,
        SemaphoreSlim semaphore,
        ReviewJob job,
        PullRequest pr,
        ReviewSystemContext executionContext,
        IChatClient effectiveClient,
        int totalChangedFileCount,
        IReadOnlyDictionary<string, int> fileIndexByPath,
        IReadOnlyDictionary<string, ReviewFileResult> existingResults,
        List<Exception> exceptions,
        CancellationToken ct)
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
        IReadOnlyList<Exception> Exceptions);
}

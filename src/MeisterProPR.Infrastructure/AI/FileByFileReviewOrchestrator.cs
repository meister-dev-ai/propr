using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.AI;

public sealed partial class FileByFileReviewOrchestrator(
    IAiReviewCore aiCore,
    IProtocolRecorder protocolRecorder,
    IJobRepository jobRepository,
    IChatClient chatClient,
    IOptions<AiReviewOptions> options,
    ILogger<FileByFileReviewOrchestrator> logger) : IFileByFileReviewOrchestrator
{
    private readonly AiReviewOptions _opts = options.Value;

    public async Task<ReviewResult> ReviewAsync(ReviewJob job, PullRequest pr, ReviewSystemContext baseContext, CancellationToken ct)
    {
        var jobWithResults = await jobRepository.GetByIdWithFileResultsAsync(job.Id, ct) ?? job;

        // Build a map of ALL existing results (completed, failed, or interrupted)
        // so we can reuse them on retry instead of hitting the UNIQUE(job_id, file_path) constraint.
        var existingResults = jobWithResults.FileReviewResults.ToDictionary(r => r.FilePath);
        var completedFiles = existingResults
            .Where(kvp => kvp.Value.IsComplete)
            .Select(kvp => kvp.Key)
            .ToHashSet();

        var filesToReview = pr.ChangedFiles.Where(f => !completedFiles.Contains(f.Path)).ToList();
        var exceptions = new List<Exception>();

        if (filesToReview.Count > 0)
        {
            using var semaphore = new SemaphoreSlim(this._opts.MaxFileReviewConcurrency);
            var tasks = filesToReview.Select(async file =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    // Find the original index for logging (1-based)
                    var allFiles = pr.ChangedFiles.ToList();
                    var fileIndex = allFiles.IndexOf(file) + 1;
                    var existingResult = existingResults.GetValueOrDefault(file.Path);
                    await this.ReviewSingleFileAsync(job, pr, file, fileIndex, allFiles.Count, baseContext, existingResult, ct);
                }
                catch (Exception ex)
                {
                    LogFileReviewFailed(logger, file.Path, job.Id, ex);
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
        }

        if (exceptions.Count > 0)
        {
            throw new PartialReviewFailureException(exceptions.Count, pr.ChangedFiles.Count, exceptions);
        }

        // US2: Synthesis
        return await this.SynthesizeResultsAsync(job, pr, ct);
    }

    private async Task ReviewSingleFileAsync(
        ReviewJob job,
        PullRequest pr,
        ChangedFile file,
        int fileIndex,
        int totalFiles,
        ReviewSystemContext baseContext,
        ReviewFileResult? existingResult,
        CancellationToken ct)
    {
        LogFileReviewStarted(logger, file.Path, fileIndex, totalFiles, job.Id);

        ReviewFileResult fileResult;
        if (existingResult is { IsComplete: false })
        {
            // Reuse the existing row (e.g., interrupted in a previous attempt).
            // Reset it so MarkCompleted / MarkFailed work correctly.
            existingResult.ResetForRetry();
            await jobRepository.UpdateFileResultAsync(existingResult, ct);
            fileResult = existingResult;
        }
        else
        {
            fileResult = new ReviewFileResult(job.Id, file.Path);
            await jobRepository.AddFileResultAsync(fileResult, ct);
        }

        Guid? protocolId = null;
        try
        {
            protocolId = await protocolRecorder.BeginAsync(job.Id, job.RetryCount + 1, file.Path, fileResult.Id, ct);
        }
        catch (Exception ex)
        {
            LogProtocolBeginFailed(logger, file.Path, job.Id, ex);
        }

        try
        {
            // US3: Filter threads for this file
            var relevantThreads = FilterThreadsForFile(pr.ExistingThreads, file.Path);

            var filePr = new PullRequest(
                pr.OrganizationUrl,
                pr.ProjectId,
                pr.RepositoryId,
                pr.PullRequestId,
                pr.IterationId,
                pr.Title,
                pr.Description,
                pr.SourceBranch,
                pr.TargetBranch,
                [file],
                pr.Status,
                relevantThreads);

            var fileContext = new ReviewSystemContext(
                baseContext.ClientSystemMessage,
                baseContext.RepositoryInstructions,
                baseContext.ReviewTools)
            {
                ActiveProtocolId = protocolId,
                ProtocolRecorder = protocolId.HasValue ? protocolRecorder : null,
                // US4: Set per-file hint so ToolAwareAiReviewCore uses per-file prompts
                PerFileHint = new PerFileReviewHint(file.Path, fileIndex, totalFiles, pr.ChangedFiles),
            };

            // US4: Custom prompts — driven by PerFileHint set above
            var result = await aiCore.ReviewAsync(filePr, fileContext, ct);

            fileResult.MarkCompleted(result.Summary, result.Comments);
            await jobRepository.UpdateFileResultAsync(fileResult, ct);

            if (protocolId.HasValue && fileContext.LoopMetrics is not null)
            {
                var m = fileContext.LoopMetrics;
                await protocolRecorder.SetCompletedAsync(
                    protocolId.Value,
                    "Completed",
                    m.TotalInputTokens,
                    m.TotalOutputTokens,
                    m.Iterations,
                    m.ToolCallCount,
                    m.FinalConfidence,
                    ct);
            }

            LogFileReviewCompleted(logger, file.Path, job.Id);
        }
        catch (Exception ex)
        {
            fileResult.MarkFailed(ex.Message);
            await jobRepository.UpdateFileResultAsync(fileResult, ct);

            if (protocolId.HasValue)
            {
                await protocolRecorder.SetCompletedAsync(protocolId.Value, "Failed", 0, 0, 0, 0, null, ct);
            }

            throw;
        }
    }

    private async Task<ReviewResult> SynthesizeResultsAsync(ReviewJob job, PullRequest pr, CancellationToken ct)
    {
        var jobWithResults = await jobRepository.GetByIdWithFileResultsAsync(job.Id, ct);
        var allResults = jobWithResults!.FileReviewResults;

        var perFileSummaries = allResults
            .Where(r => r.IsComplete && r.PerFileSummary != null)
            .Select(r => (r.FilePath, Summary: r.PerFileSummary!))
            .ToList();

        var allComments = allResults
            .Where(r => r.IsComplete && r.Comments != null)
            .SelectMany(r => r.Comments!)
            .ToList();

        // US2: Synthesis AI call
        LogSynthesisStarted(logger, job.Id);

        Guid? protocolId = null;
        try
        {
            protocolId = await protocolRecorder.BeginAsync(job.Id, job.RetryCount + 1, "synthesis", null, ct);
        }
        catch (Exception ex)
        {
            LogProtocolBeginFailed(logger, "synthesis", job.Id, ex);
        }

        string finalSummary;
        try
        {
            var systemPrompt = ReviewPrompts.BuildSynthesisSystemPrompt();
            var userMessage = ReviewPrompts.BuildSynthesisUserMessage(perFileSummaries, pr.Title, pr.Description);

            var response = await chatClient.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, systemPrompt),
                    new ChatMessage(ChatRole.User, userMessage),
                ],
                cancellationToken: ct);

            finalSummary = response.Text ?? string.Join("\n\n", perFileSummaries.Select(s => $"## {s.FilePath}\n{s.Summary}"));
            if (protocolId.HasValue)
            {
                await protocolRecorder.RecordAiCallAsync(
                    protocolId.Value,
                    1,
                    response.Usage?.InputTokenCount,
                    response.Usage?.OutputTokenCount,
                    userMessage,
                    finalSummary,
                    ct);

                await protocolRecorder.SetCompletedAsync(
                    protocolId.Value,
                    "Completed",
                    response.Usage?.InputTokenCount ?? 0,
                    response.Usage?.OutputTokenCount ?? 0,
                    1,
                    0,
                    null,
                    ct);
            }

            LogSynthesisCompleted(logger, job.Id);
        }
        catch (Exception ex)
        {
            LogSynthesisFailed(logger, job.Id, ex);
            finalSummary = string.Join("\n\n", perFileSummaries.Select(s => $"## {s.FilePath}\n{s.Summary}"));

            if (protocolId.HasValue)
            {
                await protocolRecorder.SetCompletedAsync(protocolId.Value, "Failed", 0, 0, 0, 0, null, ct);
            }
        }

        return new ReviewResult(finalSummary, allComments);
    }

    private static IReadOnlyList<PrCommentThread> FilterThreadsForFile(IReadOnlyList<PrCommentThread>? allThreads, string filePath)
    {
        if (allThreads is null)
        {
            return [];
        }

        return allThreads.Where(t => t.FilePath == filePath || t.FilePath == null).ToList();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting review for file {FilePath} ({Index}/{Total}) in job {JobId}")]
    private static partial void LogFileReviewStarted(ILogger logger, string filePath, int index, int total, Guid jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Completed review for file {FilePath} in job {JobId}")]
    private static partial void LogFileReviewCompleted(ILogger logger, string filePath, Guid jobId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed review for file {FilePath} in job {JobId}")]
    private static partial void LogFileReviewFailed(ILogger logger, string filePath, Guid jobId, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting synthesis for job {JobId}")]
    private static partial void LogSynthesisStarted(ILogger logger, Guid jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Completed synthesis for job {JobId}")]
    private static partial void LogSynthesisCompleted(ILogger logger, Guid jobId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Synthesis failed for job {JobId} — using fallback concatenation")]
    private static partial void LogSynthesisFailed(ILogger logger, Guid jobId, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to begin protocol recording for file {FilePath} in job {JobId}")]
    private static partial void LogProtocolBeginFailed(ILogger logger, string filePath, Guid jobId, Exception ex);
}

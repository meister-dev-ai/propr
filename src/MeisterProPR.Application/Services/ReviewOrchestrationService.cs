using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Application.Services;

/// <summary>
///     Orchestrates the end-to-end process of handling a review job.
/// </summary>
public sealed partial class ReviewOrchestrationService(
    IJobRepository jobs,
    IPullRequestFetcher prFetcher,
    IFileByFileReviewOrchestrator fileByFileOrchestrator,
    IAdoCommentPoster commentPoster,
    IAdoReviewerManager reviewerManager,
    IClientRegistry clientRegistry,
    IReviewPrScanRepository prScanRepository,
    IAdoThreadClient threadClient,
    IAdoThreadReplier threadReplier,
    IAiCommentResolutionCore resolutionCore,
    IReviewContextToolsFactory reviewContextToolsFactory,
    IRepositoryInstructionFetcher instructionFetcher,
    IRepositoryInstructionEvaluator instructionEvaluator,
    IOptions<AiReviewOptions> options,
    ILogger<ReviewOrchestrationService> logger)
{
    private readonly AiReviewOptions _opts = options.Value;

    /// <summary>Processes the given review job end-to-end.</summary>
    public async Task ProcessAsync(ReviewJob job, CancellationToken ct)
    {
        var reviewerId = await clientRegistry.GetReviewerIdAsync(job.ClientId, ct);
        if (reviewerId is null)
        {
            LogReviewerIdentityMissing(logger, job.ClientId, job.Id);
            await jobs.SetFailedAsync(job.Id, $"Reviewer identity not configured for client {job.ClientId}", ct);
            return;
        }

        PullRequest? pr = null;

        try
        {
            LogReviewStarted(logger, job.Id, job.PullRequestId);

            pr = await prFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                job.IterationId,
                job.ClientId,
                ct);

            if (pr.Status != PrStatus.Active)
            {
                LogPrNoLongerActive(logger, job.PullRequestId, pr.Status, job.Id);
                await jobs.SetFailedAsync(job.Id, "PR was closed or abandoned before review could begin", ct);
                return;
            }

            var scan = await prScanRepository.GetAsync(job.ClientId, job.RepositoryId, job.PullRequestId, ct);
            var iterationKey = job.IterationId.ToString();
            var isNewIteration = scan is null || scan.LastProcessedCommitId != iterationKey;

            var reviewerThreads = GetReviewerThreads(pr, reviewerId.Value);

            if (!isNewIteration && !HasNewThreadReplies(reviewerThreads, scan!, reviewerId.Value))
            {
                LogSkippedNoChange(logger, job.Id, job.PullRequestId);
                await this.SaveScanAsync(job, reviewerThreads, reviewerId.Value, ct);
                await jobs.DeleteAsync(job.Id, ct);
                return;
            }

            await reviewerManager.AddOptionalReviewerAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                reviewerId.Value,
                job.ClientId,
                ct);

            if (reviewerThreads.Count > 0)
            {
                var behavior = await clientRegistry.GetCommentResolutionBehaviorAsync(job.ClientId, ct);
                await this.EvaluateReviewerThreadsAsync(job, pr, reviewerThreads, scan, isNewIteration, behavior, reviewerId.Value, ct);
            }

            var customSystemMessage = await clientRegistry.GetCustomSystemMessageAsync(job.ClientId, ct);
            var reviewTools = reviewContextToolsFactory.Create(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                job.IterationId,
                job.ClientId);

            // Fetch repository-level instructions from the TARGET branch only (prevents prompt injection)
            var changedFilePaths = pr.ChangedFiles.Select(f => f.Path).ToList();
            var fetchedInstructions = await instructionFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                pr.TargetBranch,
                job.ClientId,
                ct);
            var relevantInstructions = fetchedInstructions.Count > 0
                ? await instructionEvaluator.EvaluateRelevanceAsync(fetchedInstructions, changedFilePaths, ct)
                : (IReadOnlyList<RepositoryInstruction>)[];

            var systemContext = new ReviewSystemContext(customSystemMessage, relevantInstructions, reviewTools);
            var result = await fileByFileOrchestrator.ReviewAsync(job, pr, systemContext, ct);

            if (string.IsNullOrWhiteSpace(result.Summary) && result.Comments.Count == 0)
            {
                LogSkippedEmptyReview(logger, job.Id, job.PullRequestId);
                await this.SaveScanAsync(job, GetReviewerThreads(pr, reviewerId.Value), reviewerId.Value, ct);
                await jobs.DeleteAsync(job.Id, ct);
                return;
            }

            await commentPoster.PostAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                job.IterationId,
                result,
                job.ClientId,
                pr.ExistingThreads,
                ct);

            await jobs.SetResultAsync(job.Id, result, ct);

            LogReviewCompleted(logger, job.Id);
        }
        catch (PartialReviewFailureException ex)
        {
            LogPartialReviewFailure(logger, job.Id, ex.FailedCount, ex.TotalCount);

            job.RetryCount++;
            await jobs.UpdateRetryCountAsync(job.Id, job.RetryCount, ct);

            if (job.RetryCount >= this._opts.MaxFileReviewRetries)
            {
                await jobs.SetFailedAsync(job.Id, $"Max retries reached. {ex.Message}", ct);
            }
            else
            {
                // Re-queue the job so the worker picks it up again without waiting for a restart.
                // FileByFileReviewOrchestrator skips already-completed file results on the next pass.
                await jobs.TryTransitionAsync(job.Id, JobStatus.Processing, JobStatus.Pending, ct);
            }

            return;
        }
        catch (Exception ex)
        {
            LogReviewFailed(logger, job.Id, ex);
            await jobs.SetFailedAsync(job.Id, ex.Message, ct);
            return;
        }

        await this.SaveScanAsync(job, GetReviewerThreads(pr!, reviewerId.Value), reviewerId.Value, ct);
    }

    private static IReadOnlyList<PrCommentThread> GetReviewerThreads(PullRequest pr, Guid reviewerId)
    {
        if (pr.ExistingThreads is null)
        {
            return [];
        }

        return pr.ExistingThreads
            .Where(t => t.Comments.FirstOrDefault()?.AuthorId == reviewerId)
            .ToList()
            .AsReadOnly();
    }

    private static bool HasNewThreadReplies(IReadOnlyList<PrCommentThread> reviewerThreads, ReviewPrScan scan, Guid reviewerId)
    {
        foreach (var thread in reviewerThreads)
        {
            var stored = scan.Threads.FirstOrDefault(t => t.ThreadId == thread.ThreadId);
            var storedCount = stored?.LastSeenReplyCount ?? 0;
            var userReplyCount = thread.Comments.Count(c => c.AuthorId != reviewerId);
            if (userReplyCount > storedCount)
            {
                return true;
            }
        }

        return false;
    }

    private async Task EvaluateReviewerThreadsAsync(
        ReviewJob job,
        PullRequest pr,
        IReadOnlyList<PrCommentThread> reviewerThreads,
        ReviewPrScan? scan,
        bool isNewIteration,
        CommentResolutionBehavior behavior,
        Guid reviewerId,
        CancellationToken ct)
    {
        if (behavior == CommentResolutionBehavior.Disabled)
        {
            return;
        }

        foreach (var thread in reviewerThreads)
        {
            // Skip threads that ADO already reports as resolved — no AI call needed.
            if (string.Equals(thread.Status, "Fixed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(thread.Status, "Closed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(thread.Status, "WontFix", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(thread.Status, "ByDesign", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                ThreadResolutionResult resolution;

                var stored = scan?.Threads.FirstOrDefault(t => t.ThreadId == thread.ThreadId);
                var storedCount = stored?.LastSeenReplyCount ?? 0;
                var userReplyCount = thread.Comments.Count(c => c.AuthorId != reviewerId);
                var hasNewReplies = userReplyCount > storedCount;

                if (isNewIteration)
                {
                    resolution = await resolutionCore.EvaluateCodeChangeAsync(thread, pr, ct);
                }
                else if (hasNewReplies)
                {
                    resolution = await resolutionCore.EvaluateConversationalReplyAsync(thread, ct);
                }
                else
                {
                    continue;
                }

                if (resolution.IsResolved)
                {
                    if (behavior == CommentResolutionBehavior.WithReply && resolution.ReplyText is not null)
                    {
                        await threadReplier.ReplyAsync(
                            job.OrganizationUrl,
                            job.ProjectId,
                            job.RepositoryId,
                            job.PullRequestId,
                            thread.ThreadId,
                            resolution.ReplyText,
                            job.ClientId,
                            ct);
                    }

                    await threadClient.UpdateThreadStatusAsync(
                        job.OrganizationUrl,
                        job.ProjectId,
                        job.RepositoryId,
                        job.PullRequestId,
                        thread.ThreadId,
                        "fixed",
                        job.ClientId,
                        ct);

                    LogThreadResolved(logger, thread.ThreadId, job.PullRequestId);
                }
                else if (!resolution.IsResolved && resolution.ReplyText is not null && !isNewIteration)
                {
                    await threadReplier.ReplyAsync(
                        job.OrganizationUrl,
                        job.ProjectId,
                        job.RepositoryId,
                        job.PullRequestId,
                        thread.ThreadId,
                        resolution.ReplyText,
                        job.ClientId,
                        ct);
                }
            }
            catch (Exception ex)
            {
                LogThreadEvaluationFailed(logger, thread.ThreadId, job.PullRequestId, ex);
            }
        }
    }

    private async Task SaveScanAsync(ReviewJob job, IReadOnlyList<PrCommentThread> reviewerThreads, Guid reviewerId, CancellationToken ct)
    {
        try
        {
            var existing = await prScanRepository.GetAsync(job.ClientId, job.RepositoryId, job.PullRequestId, ct);
            var scanId = existing?.Id ?? Guid.NewGuid();
            var scan = new ReviewPrScan(scanId, job.ClientId, job.RepositoryId, job.PullRequestId, job.IterationId.ToString());

            foreach (var thread in reviewerThreads)
            {
                scan.Threads.Add(
                    new ReviewPrScanThread
                    {
                        ReviewPrScanId = scanId,
                        ThreadId = thread.ThreadId,
                        LastSeenReplyCount = thread.Comments.Count(c => c.AuthorId != reviewerId),
                    });
            }

            await prScanRepository.UpsertAsync(scan, ct);
        }
        catch (Exception ex)
        {
            LogScanSaveFailed(logger, job.Id, ex);
        }
    }
}

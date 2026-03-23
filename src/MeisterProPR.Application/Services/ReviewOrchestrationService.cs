using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.Interfaces;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Application.Services;

/// <summary>
///     Orchestrates the end-to-end process of handling a review job.
/// </summary>
/// <param name="jobs">The job repository for managing review jobs.</param>
/// <param name="prFetcher">The pull request fetcher for retrieving PR details.</param>
/// <param name="aiCore">The AI review core for performing the review.</param>
/// <param name="commentPoster">The comment poster for posting review results to Azure DevOps.</param>
/// <param name="reviewerManager">Adds the AI identity as an optional reviewer on the PR.</param>
/// <param name="clientRegistry">Registry for looking up per-client configuration.</param>
/// <param name="prScanRepository">Tracks per-PR processing state for skip-on-no-new-commits logic.</param>
/// <param name="threadClient">Updates comment thread status in ADO.</param>
/// <param name="threadReplier">Posts replies to existing threads in ADO.</param>
/// <param name="resolutionCore">AI core for evaluating thread resolution.</param>
/// <param name="logger">The logger for logging review orchestration events.</param>
public sealed partial class ReviewOrchestrationService(
    IJobRepository jobs,
    IPullRequestFetcher prFetcher,
    IAiReviewCore aiCore,
    IAdoCommentPoster commentPoster,
    IAdoReviewerManager reviewerManager,
    IClientRegistry clientRegistry,
    IReviewPrScanRepository prScanRepository,
    IAdoThreadClient threadClient,
    IAdoThreadReplier threadReplier,
    IAiCommentResolutionCore resolutionCore,
    ILogger<ReviewOrchestrationService> logger)
{
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

            var result = await aiCore.ReviewAsync(pr, ct);

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reviewer identity not configured for client {ClientId} — failing job {JobId}")]
    private static partial void LogReviewerIdentityMissing(ILogger logger, Guid clientId, Guid jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting review for job {JobId} PR#{PrId}")]
    private static partial void LogReviewStarted(ILogger logger, Guid jobId, int prId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "PR #{PrId} is no longer active (status: {Status}) — failing job {JobId}")]
    private static partial void LogPrNoLongerActive(ILogger logger, int prId, PrStatus status, Guid jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipping review for job {JobId} PR#{PrId} — no new commits or replies")]
    private static partial void LogSkippedNoChange(ILogger logger, Guid jobId, int prId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipping review for job {JobId} PR#{PrId} — AI returned empty review")]
    private static partial void LogSkippedEmptyReview(ILogger logger, Guid jobId, int prId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Completed review for job {JobId}")]
    private static partial void LogReviewCompleted(ILogger logger, Guid jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Thread {ThreadId} on PR#{PrId} marked as fixed")]
    private static partial void LogThreadResolved(ILogger logger, int threadId, int prId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Thread {ThreadId} evaluation failed on PR#{PrId} — skipping")]
    private static partial void LogThreadEvaluationFailed(ILogger logger, int threadId, int prId, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to save ReviewPrScan for job {JobId} — processing continues")]
    private static partial void LogScanSaveFailed(ILogger logger, Guid jobId, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Review failed for job {JobId}")]
    private static partial void LogReviewFailed(ILogger logger, Guid jobId, Exception ex);
}

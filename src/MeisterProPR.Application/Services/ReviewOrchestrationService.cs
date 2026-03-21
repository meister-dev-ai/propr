using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Application.Services;

/// <summary>
///     Orchestrates the end-to-end process of handling a review job:
/// </summary>
/// <param name="jobs">The job repository for managing review jobs.</param>
/// <param name="prFetcher">The pull request fetcher for retrieving PR details.</param>
/// <param name="aiCore">The AI review core for performing the review.</param>
/// <param name="commentPoster">The comment poster for posting review results to Azure DevOps.</param>
/// <param name="reviewerManager">Adds the AI identity as an optional reviewer on the PR.</param>
/// <param name="clientRegistry">Registry for looking up per-client configuration.</param>
/// <param name="logger">The logger for logging review orchestration events.</param>
public sealed partial class ReviewOrchestrationService(
    IJobRepository jobs,
    IPullRequestFetcher prFetcher,
    IAiReviewCore aiCore,
    IAdoCommentPoster commentPoster,
    IAdoReviewerManager reviewerManager,
    IClientRegistry clientRegistry,
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

        try
        {
            LogReviewStarted(logger, job.Id, job.PullRequestId);

            var pr = await prFetcher.FetchAsync(
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

            await reviewerManager.AddOptionalReviewerAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                reviewerId.Value,
                job.ClientId,
                ct);

            var result = await aiCore.ReviewAsync(pr, ct);

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
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reviewer identity not configured for client {ClientId} — failing job {JobId}")]
    private static partial void LogReviewerIdentityMissing(ILogger logger, Guid clientId, Guid jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting review for job {JobId} PR#{PrId}")]
    private static partial void LogReviewStarted(ILogger logger, Guid jobId, int prId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "PR #{PrId} is no longer active (status: {Status}) — failing job {JobId}")]
    private static partial void LogPrNoLongerActive(ILogger logger, int prId, PrStatus status, Guid jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Completed review for job {JobId}")]
    private static partial void LogReviewCompleted(ILogger logger, Guid jobId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Review failed for job {JobId}")]
    private static partial void LogReviewFailed(ILogger logger, Guid jobId, Exception ex);
}

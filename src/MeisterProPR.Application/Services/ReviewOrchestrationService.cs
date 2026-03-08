using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
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
/// <param name="logger">The logger for logging review orchestration events.</param>
public sealed class ReviewOrchestrationService(
    IJobRepository jobs,
    IPullRequestFetcher prFetcher,
    IAiReviewCore aiCore,
    IAdoCommentPoster commentPoster,
    ILogger<ReviewOrchestrationService> logger)
{
    /// <summary>
    ///     Processes the given review job end-to-end.
    /// </summary>
    public async Task ProcessAsync(ReviewJob job, CancellationToken ct)
    {
        try
        {
            logger.LogInformation("Starting review for job {JobId} PR#{PrId}", job.Id, job.PullRequestId);

            var pr = await prFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                job.IterationId,
                ct);

            var result = await aiCore.ReviewAsync(pr, ct);

            await commentPoster.PostAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                job.IterationId,
                result,
                ct);

            jobs.SetResult(job.Id, result);
            logger.LogInformation("Completed review for job {JobId}", job.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Review failed for job {JobId}: {Error}", job.Id, ex.Message);
            jobs.SetFailed(job.Id, ex.Message);
        }
    }
}
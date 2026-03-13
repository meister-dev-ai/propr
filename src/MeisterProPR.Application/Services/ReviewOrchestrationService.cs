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
public sealed class ReviewOrchestrationService(
    IJobRepository jobs,
    IPullRequestFetcher prFetcher,
    IAiReviewCore aiCore,
    IAdoCommentPoster commentPoster,
    IAdoReviewerManager reviewerManager,
    IClientRegistry clientRegistry,
    ILogger<ReviewOrchestrationService> logger)
{
    /// <summary>
    ///     Processes the given review job end-to-end.
    /// </summary>
    public async Task ProcessAsync(ReviewJob job, CancellationToken ct)
    {
        // Guard 1: client must be associated
        if (job.ClientId is null)
        {
            logger.LogWarning("Job {JobId} has no client associated — failing", job.Id);
            jobs.SetFailed(job.Id, "No client associated with job");
            return;
        }

        // Guard 2: client must have a reviewer identity configured
        var reviewerId = await clientRegistry.GetReviewerIdAsync(job.ClientId.Value, ct);
        if (reviewerId is null)
        {
            logger.LogWarning(
                "Reviewer identity not configured for client {ClientId} — failing job {JobId}",
                job.ClientId,
                job.Id);
            jobs.SetFailed(job.Id, $"Reviewer identity not configured for client {job.ClientId}");
            return;
        }

        try
        {
            logger.LogInformation("Starting review for job {JobId} PR#{PrId}", job.Id, job.PullRequestId);

            var pr = await prFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                job.IterationId,
                job.ClientId,
                ct);

            // EC-002: PR was closed or abandoned before the review could run.
            if (pr.Status != PrStatus.Active)
            {
                logger.LogWarning(
                    "PR #{PrId} is no longer active (status: {Status}). Marking job {JobId} as failed.",
                    job.PullRequestId,
                    pr.Status,
                    job.Id);
                jobs.SetFailed(job.Id, "PR was closed or abandoned before review could begin");
                return;
            }

            // Add AI identity as optional reviewer before posting any comments.
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
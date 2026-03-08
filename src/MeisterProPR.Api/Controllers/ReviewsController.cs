using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>Manages AI pull request review jobs.</summary>
[ApiController]
[Route("[controller]")]
public sealed partial class ReviewsController(
    IJobRepository jobRepository,
    IAdoTokenValidator adoTokenValidator,
    ILogger<ReviewsController> logger) : ControllerBase
{
    private static ReviewListItem MapToListItem(ReviewJob job)
    {
        return new ReviewListItem(
            job.Id,
            job.Status,
            job.OrganizationUrl,
            job.ProjectId,
            job.RepositoryId,
            job.PullRequestId,
            job.IterationId,
            job.SubmittedAt,
            job.CompletedAt);
    }

    private static ReviewStatusResponse MapToStatusResponse(ReviewJob job)
    {
        return new ReviewStatusResponse(
            job.Id,
            job.Status,
            job.OrganizationUrl,
            job.ProjectId,
            job.RepositoryId,
            job.PullRequestId,
            job.IterationId,
            job.SubmittedAt,
            job.CompletedAt,
            job.Result is not null
                ? new ReviewResultDto(
                    job.Result.Summary,
                    job.Result.Comments.Select(c => new ReviewCommentDto(c.FilePath, c.LineNumber, c.Severity, c.Message)).ToArray())
                : null,
            job.ErrorMessage);
    }

    /// <summary>Get the status and result of a review job.</summary>
    /// <param name="adoToken">ADO personal access token used solely to verify the requesting user is an authenticated ADO organisation member.</param>
    /// <param name="jobId">The job identifier returned from POST /reviews.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Job status and, once completed, its result.</response>
    /// <response code="401">Invalid or missing client key, or invalid ADO token.</response>
    /// <response code="404">Job not found.</response>
    [HttpGet("{jobId:guid}")]
    [ProducesResponseType(typeof(ReviewStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetReview(
        [FromHeader(Name = "X-Ado-Token")] string? adoToken,
        Guid jobId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(adoToken) || !await adoTokenValidator.IsValidAsync(adoToken, ct))
        {
            return this.Unauthorized();
        }

        var job = jobRepository.GetById(jobId);
        if (job is null)
        {
            return this.NotFound();
        }

        return this.Ok(MapToStatusResponse(job));
    }

    /// <summary>List all review jobs for the current client.</summary>
    /// <param name="adoToken">ADO personal access token used solely to verify the requesting user is an authenticated ADO organisation member.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">List of review jobs, newest first.</response>
    /// <response code="401">Invalid or missing client key, or invalid ADO token.</response>
    [HttpGet]
    [ProducesResponseType(typeof(ReviewListItem[]), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListReviews(
        [FromHeader(Name = "X-Ado-Token")] string? adoToken,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(adoToken) || !await adoTokenValidator.IsValidAsync(adoToken, ct))
        {
            this.LogAdoTokenRejected();
            return this.Unauthorized();
        }

        var clientKey = this.HttpContext.Items["ClientKey"] as string ?? "";
        var jobs = jobRepository.GetAllForClient(clientKey);
        return this.Ok(jobs.Select(MapToListItem).ToArray());
    }

    /// <summary>Submit a pull request for AI review.</summary>
    /// <param name="adoToken">ADO personal access token used solely to verify the requesting user is an authenticated ADO organisation member.</param>
    /// <param name="request">The PR details to review.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="202">Review job accepted. Poll the returned jobId for status.</response>
    /// <response code="401">Invalid or missing client key, or invalid ADO token.</response>
    /// <response code="422">Request validation failed.</response>
    [HttpPost]
    [ProducesResponseType(typeof(ReviewJobResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> SubmitReview(
        [FromHeader(Name = "X-Ado-Token")] string? adoToken,
        [FromBody] ReviewRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(adoToken) || !await adoTokenValidator.IsValidAsync(adoToken, ct))
        {
            this.LogAdoTokenRejected();
            return this.Unauthorized();
        }

        var clientKey = this.HttpContext.Items["ClientKey"] as string ?? "";

        var existing = jobRepository.FindActiveJob(
            request.OrganizationUrl,
            request.ProjectId,
            request.RepositoryId,
            request.PullRequestId,
            request.IterationId);

        if (existing is not null)
        {
            return this.Accepted(new ReviewJobResponse(existing.Id));
        }

        var job = new ReviewJob(
            Guid.NewGuid(),
            clientKey,
            request.OrganizationUrl,
            request.ProjectId,
            request.RepositoryId,
            request.PullRequestId,
            request.IterationId);

        jobRepository.Add(job);

        this.LogReviewJobCreated(job.Id, job.PullRequestId);
        return this.Accepted(new ReviewJobResponse(job.Id));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "ADO token validation failed for review submission.")]
    private partial void LogAdoTokenRejected();

    [LoggerMessage(Level = LogLevel.Information, Message = "Review job {JobId} created for PR#{PrId}")]
    private partial void LogReviewJobCreated(Guid jobId, int prId);
}

/// <summary>Request payload to submit a pull request for review.</summary>
public sealed record ReviewRequest(
    string OrganizationUrl,
    string ProjectId,
    string RepositoryId,
    int PullRequestId,
    int IterationId);

/// <summary>Response returned when a review job is accepted.</summary>
public sealed record ReviewJobResponse(Guid JobId);

/// <summary>List item for a review job.</summary>
public sealed record ReviewListItem(
    Guid JobId,
    JobStatus Status,
    string OrganizationUrl,
    string ProjectId,
    string RepositoryId,
    int PullRequestId,
    int IterationId,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? CompletedAt);

/// <summary>Detailed status response for a review job.</summary>
public sealed record ReviewStatusResponse(
    Guid JobId,
    JobStatus Status,
    string OrganizationUrl,
    string ProjectId,
    string RepositoryId,
    int PullRequestId,
    int IterationId,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? CompletedAt,
    ReviewResultDto? Result,
    string? Error);

/// <summary>DTO representing the textual review result and comments.</summary>
public sealed record ReviewResultDto(string Summary, ReviewCommentDto[] Comments);

/// <summary>DTO for a single review comment.</summary>
public sealed record ReviewCommentDto(string? FilePath, int? LineNumber, CommentSeverity Severity, string Message);
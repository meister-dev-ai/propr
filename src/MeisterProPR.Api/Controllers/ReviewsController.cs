// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Extensions;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>Manages AI pull request review jobs.</summary>
[ApiController]
public sealed partial class ReviewsController(
    IJobRepository jobRepository,
    IAdoTokenValidator adoTokenValidator,
    ILogger<ReviewsController> logger,
    IPullRequestFetcher? prFetcher = null) : ControllerBase
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
    /// <param name="adoToken">
    ///     ADO personal access token used solely to verify the requesting user is an authenticated ADO
    ///     organisation member.
    /// </param>
    /// <param name="adoOrgUrl">
    ///     ADO organisation URL (e.g. https://dev.azure.com/myorg). Required when using browser-extension
    ///     session tokens; omit for PATs.
    /// </param>
    /// <param name="jobId">The job identifier returned from POST /clients/{clientId}/reviews.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Job status and, once completed, its result.</response>
    /// <response code="401">Invalid or missing ADO token.</response>
    /// <response code="404">Job not found.</response>
    [HttpGet("reviews/{jobId:guid}")]
    [ProducesResponseType(typeof(ReviewStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetReview(
        [FromHeader(Name = "X-Ado-Token")] string? adoToken,
        [FromHeader(Name = "X-Ado-Org-Url")] string? adoOrgUrl,
        Guid jobId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(adoToken) || !await adoTokenValidator.IsValidAsync(adoToken, adoOrgUrl, ct))
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

    /// <summary>List all review jobs for the specified client.</summary>
    /// <param name="clientId">ID of the client whose review jobs are listed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">List of review jobs, newest first.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks the required role for this client.</response>
    [HttpGet("clients/{clientId:guid}/reviews")]
    [ProducesResponseType(typeof(ReviewListItem[]), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult ListReviews(Guid clientId, CancellationToken ct)
    {
        var roleCheck = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientUser);
        if (roleCheck is not null)
        {
            return roleCheck;
        }

        var jobs = jobRepository.GetAllForClient(clientId);
        return this.Ok(jobs.Select(MapToListItem).ToArray());
    }

    /// <summary>Submit a pull request for AI review.</summary>
    /// <param name="clientId">ID of the client on whose behalf the review is triggered.</param>
    /// <param name="adoToken">
    ///     Azure DevOps personal access token for ADO API operations. Must never be logged or persisted.
    /// </param>
    /// <param name="request">The PR details to review.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="202">Review job accepted and queued. Returns jobId and status.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks <c>ClientAdministrator</c> role for the specified client.</response>
    /// <response code="404">Client not found.</response>
    /// <response code="409">Active review job already exists for this PR iteration.</response>
    [HttpPost("clients/{clientId:guid}/reviews")]
    [ProducesResponseType(typeof(ReviewJobAcceptedResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SubmitReview(
        Guid clientId,
        [FromHeader(Name = "X-Ado-Token")] string? adoToken,
        [FromBody] ReviewRequest request,
        CancellationToken ct)
    {
        var roleCheck = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
        if (roleCheck is not null)
        {
            return roleCheck;
        }

        if (string.IsNullOrWhiteSpace(adoToken) || !await adoTokenValidator.IsValidAsync(adoToken, request.OrganizationUrl, ct))
        {
            this.LogAdoTokenRejected(this.Request.Method, this.Request.Path);
            return this.Unauthorized();
        }

        var existing = jobRepository.FindActiveJob(
            request.OrganizationUrl,
            request.ProjectId,
            request.RepositoryId,
            request.PullRequestId,
            request.IterationId);

        if (existing is not null)
        {
            return this.Conflict(new ReviewJobAcceptedResponse(existing.Id, existing.Status.ToString().ToLowerInvariant()));
        }

        var job = new ReviewJob(
            Guid.NewGuid(),
            clientId,
            request.OrganizationUrl,
            request.ProjectId,
            request.RepositoryId,
            request.PullRequestId,
            request.IterationId);

        await jobRepository.AddAsync(job, ct);

        // Attempt to populate PR context snapshot from ADO (non-blocking — failure must not prevent job creation).
        if (prFetcher is not null)
        {
            try
            {
                var prData = await prFetcher.FetchAsync(
                    request.OrganizationUrl,
                    request.ProjectId,
                    request.RepositoryId,
                    request.PullRequestId,
                    request.IterationId,
                    clientId: clientId,
                    cancellationToken: ct);
                job.SetPrContext(prData.Title, prData.RepositoryName, prData.SourceBranch, prData.TargetBranch);
                await jobRepository.UpdatePrContextAsync(
                    job.Id, prData.Title, prData.RepositoryName, prData.SourceBranch, prData.TargetBranch, ct);
            }
            catch (Exception ex)
            {
                this.LogPrContextFetchFailed(job.Id, ex);
            }
        }

        this.LogReviewJobCreated(job.Id, job.PullRequestId);
        return this.Accepted(new ReviewJobAcceptedResponse(job.Id, JobStatus.Pending.ToString().ToLowerInvariant()));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "ADO token validation failed for {Method} {Path}.")]
    private partial void LogAdoTokenRejected(string method, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Review job {JobId} created for PR#{PrId}")]
    private partial void LogReviewJobCreated(Guid jobId, int prId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to fetch PR context for job {JobId}; continuing without PR context.")]
    private partial void LogPrContextFetchFailed(Guid jobId, Exception ex);
}

/// <summary>Request payload to submit a pull request for review.</summary>
public sealed record ReviewRequest(
    string OrganizationUrl,
    string ProjectId,
    string RepositoryId,
    int PullRequestId,
    int IterationId);

/// <summary>Response returned when a review job is accepted or a duplicate is found.</summary>
public sealed record ReviewJobAcceptedResponse(Guid JobId, string Status);

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

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Extensions;
using MeisterProPR.Api.Features.Reviewing.Contracts;
using MeisterProPR.Application.Features.Reviewing.Intake.Commands.SubmitReviewJob;
using MeisterProPR.Application.Features.Reviewing.Intake.Dtos;
using MeisterProPR.Application.Features.Reviewing.Intake.Queries.GetReviewJobStatus;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Features.Reviewing.Intake.Controllers;

/// <summary>Handles submission and status retrieval for review intake jobs.</summary>
[ApiController]
public sealed partial class ReviewJobsController(
    SubmitReviewJobHandler submitReviewJobHandler,
    GetReviewJobStatusHandler getReviewJobStatusHandler,
    IAdoTokenValidator adoTokenValidator,
    ILogger<ReviewJobsController> logger) : ControllerBase
{
    /// <summary>Get the status and result of a review job.</summary>
    /// <param name="adoToken">
    ///     ADO personal access token used solely to verify the requesting user is an authenticated ADO
    ///     organisation member.
    /// </param>
    /// <param name="adoOrgUrl">
    ///     ADO organisation URL (e.g. https://dev.azure.com/myorg). Required when using browser-extension
    ///     session tokens; omit for PATs.
    /// </param>
    /// <param name="jobId">The job identifier returned from POST /clients/{clientId}/reviewing/jobs.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Job status and, once completed, its result.</response>
    /// <response code="401">Invalid or missing ADO token.</response>
    /// <response code="404">Job not found.</response>
    [HttpGet("/reviewing/jobs/{jobId:guid}/status")]
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

        var status = await getReviewJobStatusHandler.HandleAsync(new GetReviewJobStatusQuery(jobId), ct);
        if (status is null)
        {
            return this.NotFound();
        }

        return this.Ok(MapStatusResponse(status));
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
    [HttpPost("/clients/{clientId:guid}/reviewing/jobs")]
    [HttpPost("clients/{clientId:guid}/reviews")]
    [ProducesResponseType(typeof(ReviewJobAcceptedResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SubmitReview(
        Guid clientId,
        [FromHeader(Name = "X-Ado-Token")] string? adoToken,
        [FromBody] SubmitReviewJobRequestDto request,
        CancellationToken ct)
    {
        var roleCheck = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
        if (roleCheck is not null)
        {
            return roleCheck;
        }

        if (string.IsNullOrWhiteSpace(adoToken) || !await adoTokenValidator.IsValidAsync(adoToken, request.OrganizationUrl, ct))
        {
            LogAdoTokenRejected(logger, this.Request.Method, this.Request.Path);
            return this.Unauthorized();
        }

        var result = await submitReviewJobHandler.HandleAsync(new SubmitReviewJobCommand(clientId, request), ct);
        var response = new ReviewJobAcceptedResponse(result.JobId, result.Status.ToString().ToLowerInvariant());

        if (result.IsDuplicate)
        {
            return this.Conflict(response);
        }

        LogReviewJobCreated(logger, result.JobId, request.PullRequestId);
        return this.Accepted(response);
    }

    private static ReviewStatusResponse MapStatusResponse(ReviewJobStatusDto status)
    {
        return new ReviewStatusResponse(
            status.JobId,
            status.Status,
            status.OrganizationUrl,
            status.ProjectId,
            status.RepositoryId,
            status.PullRequestId,
            status.IterationId,
            status.SubmittedAt,
            status.CompletedAt,
            status.Result is null
                ? null
                : new ReviewResultDto(
                    status.Result.Summary,
                    status.Result.Comments.Select(comment => new ReviewCommentDto(comment.FilePath, comment.LineNumber, comment.Severity, comment.Message)).ToArray()),
            status.Error);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "ADO token validation failed for {Method} {Path}.")]
    private static partial void LogAdoTokenRejected(ILogger logger, string method, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Review job {JobId} created for PR#{PrId}")]
    private static partial void LogReviewJobCreated(ILogger logger, Guid jobId, int prId);
}

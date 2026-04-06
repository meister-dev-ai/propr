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
public sealed class ReviewsController(IJobRepository jobRepository) : ControllerBase
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

    /// <summary>List all review jobs for the specified client.</summary>
    /// <param name="clientId">ID of the client whose review jobs are listed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">List of review jobs, newest first.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks the required role for this client.</response>
    [HttpGet("/clients/{clientId:guid}/reviewing/jobs")]
    [HttpGet("/clients/{clientId:guid}/reviews")]
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
}

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

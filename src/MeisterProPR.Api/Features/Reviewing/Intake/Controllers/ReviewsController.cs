// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Extensions;
using MeisterProPR.Api.Features.Reviewing.Contracts;
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
            job.CompletedAt)
        {
            Provider = job.Provider,
            HostBaseUrl = job.HostBaseUrl,
            Repository = new ReviewRepositoryRefDto(
                job.RepositoryReference.ExternalRepositoryId,
                job.RepositoryReference.OwnerOrNamespace,
                job.RepositoryReference.ProjectPath),
            CodeReview = new ReviewCodeReviewRefDto(
                job.CodeReviewReference.Platform,
                job.CodeReviewReference.ExternalReviewId,
                job.CodeReviewReference.Number),
            ReviewRevision = job.ReviewRevisionReference is null
                ? null
                : new ReviewRevisionRefDto(
                    job.ReviewRevisionReference.HeadSha,
                    job.ReviewRevisionReference.BaseSha,
                    job.ReviewRevisionReference.StartSha,
                    job.ReviewRevisionReference.ProviderRevisionId,
                    job.ReviewRevisionReference.PatchIdentity),
        };
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
    string ProviderScopePath,
    string ProviderProjectKey,
    string RepositoryId,
    int PullRequestId,
    int IterationId,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? CompletedAt)
{
    /// <summary>Normalized provider family for the review job.</summary>
    public ScmProvider Provider { get; init; } = ScmProvider.AzureDevOps;

    /// <summary>Normalized provider host base URL for the review job.</summary>
    public string? HostBaseUrl { get; init; }

    /// <summary>Normalized repository identity for the review job.</summary>
    public ReviewRepositoryRefDto? Repository { get; init; }

    /// <summary>Normalized code review identity for the review job.</summary>
    public ReviewCodeReviewRefDto? CodeReview { get; init; }

    /// <summary>Normalized review revision identity for the review job.</summary>
    public ReviewRevisionRefDto? ReviewRevision { get; init; }
}

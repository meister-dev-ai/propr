// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Extensions;
using MeisterDev.ProPR.Api.Features.Reviewing.Contracts;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Features.Reviewing.Intake.Ports;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using MeisterDev.ProPR.Web;

namespace MeisterDev.ProPR.Api.Controllers;

/// <summary>Manages AI pull request review jobs.</summary>
[ApiController]
[Route("clients/{clientId:guid}")]
public sealed class ReviewsController(IJobRepository jobRepository, ICustomerDashboardReader dashboardReader) : ControllerBase
{
    /// <summary>Reads a bounded page of persisted review metadata for the specified client.</summary>
    /// <param name="clientId">The owning client.</param>
    /// <param name="reader">The client history projection.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="pageSize">Number of rows, between 1 and 100.</param>
    /// <param name="status">Optional named job status.</param>
    [HttpGet("reviewing/history")]
    [ProducesResponseType(typeof(CustomerReviewHistory), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetHistory(
        Guid clientId, [FromServices] ICustomerReviewHistoryReader reader,
        CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, [FromQuery] string? status = null)
    {
        var roleCheck = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientUser);
        if (roleCheck is not null)
        {
            return roleCheck;
        }

        JobStatus? filter = null;
        if (status is not null)
        {
            if (!Enum.TryParse<JobStatus>(status, true, out var parsed) || !Enum.IsDefined(parsed) ||
                !Enum.GetNames<JobStatus>().Any(name => string.Equals(name, status, StringComparison.OrdinalIgnoreCase)))
            {
                return this.BadRequest();
            }

            filter = parsed;
        }

        if (page < 1 || pageSize is < 1 or > 100 || page > int.MaxValue / pageSize)
        {
            return this.BadRequest();
        }

        return this.Ok(await reader.GetAsync(clientId, page, pageSize, filter, ct).ConfigureAwait(false));
    }

    /// <summary>Get processing reviews and findings from jobs completed in the previous 30 days.</summary>
    /// <remarks>The completion-time window includes <c>windowStart</c> and excludes <c>windowEnd</c>.</remarks>
    /// <param name="clientId">ID of the client whose dashboard is read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Review activity for the client.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks the required role for this client.</response>
    [HttpGet("reviewing/dashboard")]
    [ProducesResponseType(typeof(CustomerDashboard), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetDashboard(Guid clientId, CancellationToken ct)
    {
        var roleCheck = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientUser);
        if (roleCheck is not null)
        {
            return roleCheck;
        }

        return this.Ok(await dashboardReader.GetAsync(clientId, DateTimeOffset.UtcNow, ct).ConfigureAwait(false));
    }

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
    [HttpGet("reviewing/jobs")]
    [HttpGet("reviews")]
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
    public ScmProvider Provider { get; init; } = ScmProviderVocabulary.AzureDevOpsCompatibilityDefault;

    /// <summary>Normalized provider host base URL for the review job.</summary>
    public string? HostBaseUrl { get; init; }

    /// <summary>Normalized repository identity for the review job.</summary>
    public ReviewRepositoryRefDto? Repository { get; init; }

    /// <summary>Normalized code review identity for the review job.</summary>
    public ReviewCodeReviewRefDto? CodeReview { get; init; }

    /// <summary>Normalized review revision identity for the review job.</summary>
    public ReviewRevisionRefDto? ReviewRevision { get; init; }
}

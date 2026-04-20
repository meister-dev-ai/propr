// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Extensions;
using MeisterProPR.Api.Features.Reviewing.Contracts;
using MeisterProPR.Application.Features.Reviewing.Intake.Commands.SubmitReviewJob;
using MeisterProPR.Application.Features.Reviewing.Intake.Dtos;
using MeisterProPR.Application.Features.Reviewing.Intake.Queries.GetReviewJobStatus;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Features.Reviewing.Intake.Controllers;

/// <summary>Handles submission and status retrieval for review intake jobs.</summary>
[ApiController]
public sealed partial class ReviewJobsController(
    SubmitReviewJobHandler submitReviewJobHandler,
    GetReviewJobStatusHandler getReviewJobStatusHandler,
    ILogger<ReviewJobsController> logger) : ControllerBase
{
    /// <summary>Get the status and result of a review job.</summary>
    /// <param name="jobId">The job identifier returned from POST /clients/{clientId}/reviewing/jobs.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Job status and, once completed, its result.</response>
    /// <response code="401">Missing or invalid app authentication.</response>
    /// <response code="403">Caller lacks access to the job's owning client.</response>
    /// <response code="404">Job not found.</response>
    [HttpGet("/reviewing/jobs/{jobId:guid}/status")]
    [HttpGet("reviews/{jobId:guid}")]
    [ProducesResponseType(typeof(ReviewStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetReview(Guid jobId, CancellationToken ct)
    {
        var auth = AuthHelpers.RequireAuthenticated(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        var status = await getReviewJobStatusHandler.HandleAsync(new GetReviewJobStatusQuery(jobId), ct);
        if (status is null)
        {
            return this.NotFound();
        }

        var roleCheck = AuthHelpers.RequireClientRole(this.HttpContext, status.ClientId, ClientRole.ClientUser);
        if (roleCheck is not null)
        {
            return roleCheck;
        }

        return this.Ok(MapStatusResponse(status));
    }

    /// <summary>Submit a pull request for AI review.</summary>
    /// <param name="clientId">ID of the client on whose behalf the review is triggered.</param>
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
        [FromBody] SubmitReviewRequest request,
        CancellationToken ct)
    {
        var roleCheck = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
        if (roleCheck is not null)
        {
            return roleCheck;
        }

        if (!TryMapRequest(request, out var intakeRequest, out var validationError))
        {
            return this.BadRequest(new { error = validationError });
        }

        var result = await submitReviewJobHandler.HandleAsync(new SubmitReviewJobCommand(clientId, intakeRequest), ct);
        var response = MapAcceptedResponse(result, intakeRequest);

        if (result.IsDuplicate)
        {
            return this.Conflict(response);
        }

        LogReviewJobCreated(logger, result.JobId, intakeRequest.CodeReview?.Number ?? intakeRequest.PullRequestId);
        return this.Accepted(response);
    }

    private static ReviewStatusResponse MapStatusResponse(ReviewJobStatusDto status)
    {
        return new ReviewStatusResponse(
            status.JobId,
            status.Status,
            status.ProviderScopePath,
            status.ProviderProjectKey,
            status.RepositoryId,
            status.PullRequestId,
            status.IterationId,
            status.SubmittedAt,
            status.CompletedAt,
            status.Result is null
                ? null
                : new ReviewResultDto(
                    status.Result.Summary,
                    status.Result.Comments.Select(comment => new ReviewCommentDto(
                            comment.FilePath,
                            comment.LineNumber,
                            comment.Severity,
                            comment.Message))
                        .ToArray()),
            status.Error)
        {
            Provider = status.Provider,
            HostBaseUrl = status.Host?.HostBaseUrl,
            Repository = MapRepository(status.Repository),
            CodeReview = MapCodeReview(status.CodeReview),
            ReviewRevision = MapReviewRevision(status.ReviewRevision),
        };
    }

    private static ReviewJobAcceptedResponse MapAcceptedResponse(
        SubmitReviewJobResult result,
        SubmitReviewJobRequestDto request)
    {
        return new ReviewJobAcceptedResponse(
            result.JobId,
            result.Status.ToString().ToLowerInvariant(),
            request.Provider,
            request.Host?.HostBaseUrl,
            MapRepository(request.Repository),
            MapCodeReview(request.CodeReview),
            MapReviewRevision(request.ReviewRevision));
    }

    private static bool TryMapRequest(
        SubmitReviewRequest request,
        out SubmitReviewJobRequestDto intakeRequest,
        out string? validationError)
    {
        intakeRequest = null!;
        validationError = null;

        var provider = request.Provider;
        if (!provider.HasValue)
        {
            validationError = "Provider is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.HostBaseUrl))
        {
            validationError = "HostBaseUrl is required.";
            return false;
        }

        ProviderHostRef host;
        try
        {
            host = new ProviderHostRef(provider.Value, request.HostBaseUrl);
        }
        catch (ArgumentException ex)
        {
            validationError = ex.Message;
            return false;
        }

        if (request.Repository is null)
        {
            validationError = "Repository information is required.";
            return false;
        }

        RepositoryRef repository;
        try
        {
            repository = new RepositoryRef(
                host,
                request.Repository.ExternalRepositoryId,
                request.Repository.OwnerOrNamespace,
                request.Repository.ProjectPath);
        }
        catch (ArgumentException ex)
        {
            validationError = ex.Message;
            return false;
        }

        if (request.CodeReview is null)
        {
            validationError = "Code review details are required.";
            return false;
        }

        CodeReviewRef codeReview;
        try
        {
            codeReview = new CodeReviewRef(
                repository,
                request.CodeReview.Platform,
                request.CodeReview.ExternalReviewId,
                request.CodeReview.Number);
        }
        catch (ArgumentException ex)
        {
            validationError = ex.Message;
            return false;
        }

        ReviewRevision? reviewRevision = null;
        if (request.ReviewRevision is not null)
        {
            try
            {
                reviewRevision = new ReviewRevision(
                    request.ReviewRevision.HeadSha,
                    request.ReviewRevision.BaseSha,
                    request.ReviewRevision.StartSha,
                    request.ReviewRevision.ProviderRevisionId,
                    request.ReviewRevision.PatchIdentity);
            }
            catch (ArgumentException ex)
            {
                validationError = ex.Message;
                return false;
            }
        }

        ReviewerIdentity? requestedReviewerIdentity = null;
        if (request.RequestedReviewerIdentity is not null)
        {
            requestedReviewerIdentity = new ReviewerIdentity(
                host,
                request.RequestedReviewerIdentity.ExternalUserId,
                request.RequestedReviewerIdentity.Login,
                request.RequestedReviewerIdentity.DisplayName,
                request.RequestedReviewerIdentity.IsBot);
        }

        var providerScopePath = host.HostBaseUrl;
        var providerProjectKey = ResolveProviderProjectKey(repository);
        var repositoryId = repository.ExternalRepositoryId;
        var pullRequestId = codeReview.Number;
        var iterationId = DeriveIterationId(reviewRevision);

        intakeRequest = new SubmitReviewJobRequestDto(
            providerScopePath,
            providerProjectKey,
            repositoryId,
            pullRequestId,
            iterationId)
        {
            Provider = provider.Value,
            Host = host,
            Repository = repository,
            CodeReview = codeReview,
            ReviewRevision = reviewRevision,
            RequestedReviewerIdentity = requestedReviewerIdentity,
        };

        return true;
    }

    private static int DeriveIterationId(ReviewRevision? reviewRevision)
    {
        if (reviewRevision is { ProviderRevisionId: not null } &&
            int.TryParse(reviewRevision.ProviderRevisionId, out var parsedIterationId) && parsedIterationId > 0)
        {
            return parsedIterationId;
        }

        return 1;
    }

    private static string ResolveProviderProjectKey(RepositoryRef repository)
    {
        if (!string.IsNullOrWhiteSpace(repository.OwnerOrNamespace))
        {
            return repository.OwnerOrNamespace;
        }

        return repository.ProjectPath;
    }

    private static ReviewRepositoryRefDto? MapRepository(RepositoryRef? repository)
    {
        return repository is null
            ? null
            : new ReviewRepositoryRefDto(
                repository.ExternalRepositoryId,
                repository.OwnerOrNamespace,
                repository.ProjectPath);
    }

    private static ReviewCodeReviewRefDto? MapCodeReview(CodeReviewRef? review)
    {
        return review is null
            ? null
            : new ReviewCodeReviewRefDto(review.Platform, review.ExternalReviewId, review.Number);
    }

    private static ReviewRevisionRefDto? MapReviewRevision(ReviewRevision? reviewRevision)
    {
        return reviewRevision is null
            ? null
            : new ReviewRevisionRefDto(
                reviewRevision.HeadSha,
                reviewRevision.BaseSha,
                reviewRevision.StartSha,
                reviewRevision.ProviderRevisionId,
                reviewRevision.PatchIdentity);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Review job {JobId} created for PR#{PrId}")]
    private static partial void LogReviewJobCreated(ILogger logger, Guid jobId, int prId);
}

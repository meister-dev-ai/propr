// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Extensions;
using MeisterProPR.Api.Features.Licensing;
using MeisterProPR.Api.Features.Reviewing.Contracts;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Features.Reviewing.Intake.Commands.RestartReviewJob;
using MeisterProPR.Application.Features.Reviewing.Intake.Commands.StopReviewJob;
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
    RestartReviewJobHandler restartReviewJobHandler,
    StopReviewJobHandler stopReviewJobHandler,
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
    /// <response code="409">An active review job already exists for this PR iteration, or the pull request is blocked from processing.</response>
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

        SubmitReviewJobResult result;
        try
        {
            result = await submitReviewJobHandler.HandleAsync(new SubmitReviewJobCommand(clientId, intakeRequest), ct);
        }
        catch (InvalidOperationException ex)
        {
            return this.BadRequest(new { error = ex.Message });
        }
        catch (PremiumFeatureUnavailableException ex)
        {
            return new PremiumFeatureUnavailableResult(ex.Capability);
        }

        if (result.IsBlocked)
        {
            return this.Conflict(new { error = "This pull request is blocked from review processing." });
        }

        var response = MapAcceptedResponse(result, intakeRequest);

        if (result.IsDuplicate)
        {
            return this.Conflict(response);
        }

        LogReviewJobCreated(logger, result.JobId, intakeRequest.CodeReview?.Number ?? intakeRequest.PullRequestId);
        return this.Accepted(response);
    }

    /// <summary>Manually restart a failed review job.</summary>
    /// <remarks>
    ///     Failed reviews are not auto-continued (to avoid looping on deterministic failures), so a restart must be
    ///     triggered explicitly. Any user with at least <see cref="ClientRole.ClientUser" /> for the job's owning client
    ///     may restart it — administrator rights are not required.
    /// </remarks>
    /// <param name="jobId">The identifier of the failed review job to restart.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="202">Restart accepted; a new pending review job was queued.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks access to the job's owning client.</response>
    /// <response code="404">Job not found.</response>
    /// <response code="409">Job is not in a failed state, or an active job already exists for this PR revision.</response>
    [HttpPost("/reviewing/jobs/{jobId:guid}/restart")]
    [HttpPost("/jobs/{jobId:guid}/restart")]
    [ProducesResponseType(typeof(ReviewJobRestartResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RestartReview(Guid jobId, CancellationToken ct)
    {
        var auth = AuthHelpers.RequireAuthenticated(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        // Resolve the owning client first so the role check happens before any mutation.
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

        var result = await restartReviewJobHandler.HandleAsync(new RestartReviewJobCommand(jobId), ct);

        switch (result.Outcome)
        {
            case RestartReviewJobOutcome.NotFound:
                return this.NotFound();
            case RestartReviewJobOutcome.NotFailed:
                return this.Conflict(new { error = "Only failed review jobs can be restarted." });
            case RestartReviewJobOutcome.DuplicateActiveJob:
                return this.Conflict(new { error = "An active review job already exists for this pull request revision." });
            case RestartReviewJobOutcome.Restarted:
            default:
                LogReviewJobRestarted(logger, jobId, result.NewJobId ?? Guid.Empty);
                return this.Accepted(
                    new ReviewJobRestartResponse(
                        result.NewJobId ?? Guid.Empty,
                        jobId,
                        JobStatus.Pending.ToString().ToLowerInvariant()));
        }
    }

    /// <summary>Manually stop a running or queued review job.</summary>
    /// <remarks>
    ///     Lets a client administrator halt a review that should not run to completion (for example an
    ///     oversized or misbehaving pull request). Requires <see cref="ClientRole.ClientAdministrator" /> for
    ///     the job's owning client. Stopping is terminal: it does not requeue the job. Blocking a pull request
    ///     is a separate action that prevents future processing without stopping the current run.
    /// </remarks>
    /// <param name="jobId">The identifier of the review job to stop.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The job was running or queued and has been stopped.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks <c>ClientAdministrator</c> rights for the job's owning client.</response>
    /// <response code="404">Job not found.</response>
    /// <response code="409">Job has already reached a terminal state and cannot be stopped.</response>
    [HttpPost("/reviewing/jobs/{jobId:guid}/stop")]
    [HttpPost("/jobs/{jobId:guid}/stop")]
    [ProducesResponseType(typeof(ReviewJobStopResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> StopReview(Guid jobId, CancellationToken ct)
    {
        var auth = AuthHelpers.RequireAuthenticated(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        // Resolve the owning client first so the administrator role check happens before any mutation.
        var status = await getReviewJobStatusHandler.HandleAsync(new GetReviewJobStatusQuery(jobId), ct);
        if (status is null)
        {
            return this.NotFound();
        }

        var roleCheck = AuthHelpers.RequireClientRole(this.HttpContext, status.ClientId, ClientRole.ClientAdministrator);
        if (roleCheck is not null)
        {
            return roleCheck;
        }

        var result = await stopReviewJobHandler.HandleAsync(new StopReviewJobCommand(jobId), ct);

        switch (result.Outcome)
        {
            case StopReviewJobOutcome.NotFound:
                return this.NotFound();
            case StopReviewJobOutcome.AlreadyFinished:
                return this.Conflict(new { error = "Only running or queued review jobs can be stopped." });
            case StopReviewJobOutcome.Stopped:
                LogReviewJobStopped(logger, jobId);
                return this.Ok(new ReviewJobStopResponse(jobId, JobStatus.Stopped.ToString().ToLowerInvariant()));
            default:
                return this.StatusCode(StatusCodes.Status500InternalServerError, new { error = "Unexpected stop outcome." });
        }
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
            Workspace = status.Workspace is null
                ? null
                : new ReviewWorkspaceStatusDto(
                    status.Workspace.Attempted,
                    status.Workspace.Prepared,
                    status.Workspace.FallbackApplied,
                    status.Workspace.WorkspaceKey,
                    status.Workspace.FailureStage,
                    status.Workspace.FailureCode,
                    status.Workspace.FailureMessage),
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Failed review job {SourceJobId} restarted as new job {NewJobId}")]
    private static partial void LogReviewJobRestarted(ILogger logger, Guid sourceJobId, Guid newJobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Review job {JobId} stopped by client administrator")]
    private static partial void LogReviewJobStopped(ILogger logger, Guid jobId);
}

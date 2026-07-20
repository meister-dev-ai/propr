// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Extensions;
using MeisterProPR.Api.Features.Reviewing.Contracts;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Diagnostics.Ports;
using MeisterProPR.Application.Features.Reviewing.Diagnostics.Queries.GetFileDiff;
using MeisterProPR.Application.Features.Reviewing.Diagnostics.Queries.GetReviewJobProtocol;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>Provides a global view of all review jobs across all clients (admin only).</summary>
[ApiController]
[Route("reviewing/jobs")]
public sealed class JobsController(
    IJobRepository jobRepository,
    GetReviewJobProtocolHandler getReviewJobProtocolHandler,
    GetFileDiffHandler getFileDiffHandler) : ControllerBase
{
    /// <summary>
    ///     Returns all review jobs across all clients, newest first.
    ///     Requires an Admin JWT or <c>X-User-Pat</c> for unrestricted access, or valid user authentication for scoped client
    ///     access.
    /// </summary>
    /// <param name="limit">Maximum number of items to return (1–1000, default 100).</param>
    /// <param name="offset">Number of items to skip for pagination (default 0).</param>
    /// <param name="status">Optional status filter: Pending, Processing, Completed, or Failed.</param>
    /// <param name="clientId">Optional client filter: only return jobs for this client.</param>
    /// <param name="pullRequestId">Optional pull request filter: only return jobs for this pull request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paginated list of all jobs.</returns>
    /// <response code="200">Jobs returned.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    [HttpGet]
    [HttpGet("/jobs")]
    [ProducesResponseType(typeof(JobListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetAllJobs(
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        [FromQuery] JobStatus? status = null,
        [FromQuery] Guid? clientId = null,
        [FromQuery] int? pullRequestId = null,
        CancellationToken cancellationToken = default)
    {
        var auth = AuthHelpers.RequireAuthenticated(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        var isAdmin = AuthHelpers.IsAdmin(this.HttpContext);

        if (!isAdmin)
        {
            var clientRoles = AuthHelpers.GetClientRoles(this.HttpContext);
            if (clientId.HasValue)
            {
                if (!clientRoles.ContainsKey(clientId.Value))
                {
                    return this.StatusCode(StatusCodes.Status403Forbidden, new { error = "Access denied." });
                }
            }
            else
            {
                if (clientRoles.Count == 0)
                {
                    return this.Ok(new JobListResponse(0, []));
                }

                if (clientRoles.Count == 1)
                {
                    clientId = clientRoles.Keys.First();
                }
            }
        }

        limit = Math.Clamp(limit, 1, 1000);
        offset = Math.Max(offset, 0);

        var (total, items) = await jobRepository.GetJobListPageAsync(
            limit,
            offset,
            status,
            clientId,
            pullRequestId,
            cancellationToken);

        return this.Ok(
            new JobListResponse(
                total,
                items.Select(j => new JobListItem(
                        j.Id,
                        j.ClientId,
                        j.OrganizationUrl,
                        j.ProjectId,
                        j.RepositoryId,
                        j.PullRequestId,
                        j.IterationId,
                        j.Status,
                        j.SubmittedAt,
                        j.ProcessingStartedAt,
                        j.CompletedAt,
                        j.ResultSummary,
                        j.ErrorMessage,
                        j.TotalInputTokens,
                        j.TotalOutputTokens,
                        j.PrTitle,
                        j.PrSourceBranch,
                        j.PrTargetBranch,
                        j.PrRepositoryName,
                        j.AiModel,
                        j.FilesReviewed,
                        j.FilesInScope,
                        j.TotalEstimatedCostUsd,
                        j.CostIsApproximate,
                        j.BudgetSoftCapped))
                    .ToList()));
    }

    /// <summary>Returns detail for a single review job including per-tier token breakdown.</summary>
    /// <param name="id">The review job identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Job detail with token breakdown, or 404 if not found.</returns>
    /// <response code="200">Job detail returned.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="404">Job not found.</response>
    [HttpGet("{id:guid}")]
    [HttpGet("/jobs/{id:guid}")]
    [ProducesResponseType(typeof(JobDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetJob(Guid id, CancellationToken cancellationToken = default)
    {
        var auth = AuthHelpers.RequireAuthenticated(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        var job = jobRepository.GetById(id);
        if (job is null)
        {
            return this.NotFound();
        }

        var roleCheck = AuthHelpers.RequireClientRole(this.HttpContext, job.ClientId, ClientRole.ClientUser);
        if (roleCheck is not null)
        {
            return roleCheck;
        }

        var breakdown = job.TokenBreakdown;
        bool? breakdownConsistent = null;
        if (breakdown.Count > 0)
        {
            var breakdownInput = breakdown.Sum(e => e.TotalInputTokens);
            var breakdownOutput = breakdown.Sum(e => e.TotalOutputTokens);
            breakdownConsistent = breakdownInput == (job.TotalInputTokensAggregated ?? 0)
                                  && breakdownOutput == (job.TotalOutputTokensAggregated ?? 0);
        }

        // Progress metric: live numerator via a projection-only count (no file-result text), fixed
        // denominator from the job's snapshotted column.
        var filesReviewed = await jobRepository.CountReviewedFilesAsync(id, cancellationToken);

        var budgetStatus = job.BudgetBlockScope is { } budgetScope
            ? new BudgetStatusDto(
                budgetScope,
                job.BudgetBlockCapKind ?? BudgetCapKind.Hard,
                job.BudgetBlockThresholdUsd ?? 0m,
                job.BudgetBlockSpentUsd ?? 0m)
            : null;

        return this.Ok(
            new JobDetailResponse(
                job.Id,
                job.ClientId,
                job.Status,
                job.SubmittedAt,
                job.ProcessingStartedAt,
                job.CompletedAt,
                job.TotalInputTokensAggregated,
                job.TotalOutputTokensAggregated,
                job.ErrorMessage,
                job.AiModel,
                job.ReviewTemperature,
                breakdown,
                breakdownConsistent,
                filesReviewed,
                job.InScopeChangedFileCount,
                job.TotalEstimatedCostUsd,
                job.CostIsApproximate,
                budgetStatus));
    }

    /// <summary>Returns the review result (summary and comments) for a completed job.</summary>
    /// <param name="id">The review job identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The review result, or 404 if the job has no result yet.</returns>
    /// <response code="200">Review result returned.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="404">Job not found or result not yet available.</response>
    [HttpGet("{id:guid}/result")]
    [HttpGet("/jobs/{id:guid}/result")]
    [ProducesResponseType(typeof(ReviewJobResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetJobResult(Guid id, CancellationToken cancellationToken = default)
    {
        var auth = AuthHelpers.RequireAuthenticated(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        var job = jobRepository.GetById(id);
        if (job is null)
        {
            return this.NotFound();
        }

        var roleCheck = AuthHelpers.RequireClientRole(this.HttpContext, job.ClientId, ClientRole.ClientUser);
        if (roleCheck is not null)
        {
            return roleCheck;
        }

        if (job.Result is null)
        {
            return this.NotFound();
        }

        return this.Ok(
            new ReviewJobResultDto(
                job.Id,
                job.ClientId,
                job.Status,
                job.SubmittedAt,
                job.CompletedAt,
                new ReviewResultDto(
                    job.Result.Summary,
                    job.Result.Comments
                        .Select(c => new ReviewCommentDto(
                            c.FilePath, c.LineNumber, c.Severity, c.Message, c.OriginPassKind, c.ScopeRelation, c.OriginPassIndex, c.OriginPassLens))
                        .ToArray())));
    }

    /// <summary>
    ///     Returns the protocol (agentic trace) for a single review job.
    /// </summary>
    /// <param name="id">The review job identifier.</param>
    /// <param name="includeEvents">When false, omits heavy per-event bodies from the list response while retaining event rows and metadata.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The protocol records with all captured events, or 404 if the job has no protocols.</returns>
    /// <response code="200">Protocols returned.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="404">Job not found.</response>
    [HttpGet("{id:guid}/protocol")]
    [HttpGet("/jobs/{id:guid}/protocol")]
    [ProducesResponseType(typeof(IReadOnlyList<ReviewJobProtocolDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetJobProtocol(
        Guid id,
        [FromQuery] bool includeEvents = true,
        CancellationToken cancellationToken = default)
    {
        var auth = AuthHelpers.RequireAuthenticated(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        var protocolResult = await getReviewJobProtocolHandler.HandleAsync(
            new GetReviewJobProtocolQuery(id, includeEvents),
            cancellationToken);
        if (protocolResult is null)
        {
            return this.NotFound();
        }

        var roleCheck = AuthHelpers.RequireClientRole(this.HttpContext, protocolResult.ClientId, ClientRole.ClientUser);
        if (roleCheck is not null)
        {
            return roleCheck;
        }

        if (protocolResult.Protocols.Count == 0)
        {
            return this.NotFound();
        }

        return this.Ok(protocolResult.Protocols);
    }

    /// <summary>
    ///     Returns one full protocol pass (including captured event bodies) for a single review job.
    /// </summary>
    /// <param name="id">The review job identifier.</param>
    /// <param name="protocolId">The protocol-pass identifier.</param>
    /// <param name="diagnosticsReader">Diagnostics reader used to load the captured protocol pass.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Protocol pass returned.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="404">Job or protocol pass not found.</response>
    [HttpGet("{id:guid}/protocol/{protocolId:guid}")]
    [HttpGet("/jobs/{id:guid}/protocol/{protocolId:guid}")]
    [ProducesResponseType(typeof(ReviewJobProtocolDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetJobProtocolPass(
        Guid id,
        Guid protocolId,
        [FromServices] IReviewDiagnosticsReader diagnosticsReader,
        CancellationToken cancellationToken = default)
    {
        var auth = AuthHelpers.RequireAuthenticated(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        var job = jobRepository.GetById(id);
        if (job is null)
        {
            return this.NotFound();
        }

        var roleCheck = AuthHelpers.RequireClientRole(this.HttpContext, job.ClientId, ClientRole.ClientUser);
        if (roleCheck is not null)
        {
            return roleCheck;
        }

        var protocolPass = await diagnosticsReader.GetJobProtocolPassAsync(id, protocolId, cancellationToken);
        return protocolPass is null ? this.NotFound() : this.Ok(protocolPass);
    }

    /// <summary>
    ///     Returns the unified diff that was reviewed for a single file result on a review job.
    ///     The diff is re-fetched on demand from the source control provider using the job's stored coordinates.
    /// </summary>
    /// <param name="jobId">The review job identifier.</param>
    /// <param name="fileResultId">The file result identifier whose diff should be returned.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    ///     A <see cref="FileDiffDto" /> describing the diff and its availability, or a 404 if the job
    ///     or file result cannot be located.
    /// </returns>
    /// <response code="200">File diff (or availability) returned.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="404">Job or file result not found.</response>
    [HttpGet("{jobId:guid}/files/{fileResultId:guid}/diff")]
    [HttpGet("/jobs/{jobId:guid}/files/{fileResultId:guid}/diff")]
    [ProducesResponseType(typeof(FileDiffDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetFileDiff(
        Guid jobId,
        Guid fileResultId,
        CancellationToken cancellationToken = default)
    {
        var auth = AuthHelpers.RequireAuthenticated(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        var job = jobRepository.GetById(jobId);
        if (job is null)
        {
            return this.NotFound();
        }

        var roleCheck = AuthHelpers.RequireClientRole(this.HttpContext, job.ClientId, ClientRole.ClientUser);
        if (roleCheck is not null)
        {
            return roleCheck;
        }

        var result = await getFileDiffHandler.HandleAsync(new GetFileDiffQuery(jobId, fileResultId), cancellationToken);

        if (string.Equals(result.Availability, FileDiffAvailability.NotFound, StringComparison.Ordinal)
            && string.IsNullOrEmpty(result.FilePath))
        {
            return this.NotFound();
        }

        return this.Ok(result);
    }

    /// <summary>Single job item in the list response.</summary>
    public sealed record JobListItem(
        Guid Id,
        Guid? ClientId,
        string ProviderScopePath,
        string ProviderProjectKey,
        string RepositoryId,
        int PullRequestId,
        int IterationId,
        JobStatus Status,
        DateTimeOffset SubmittedAt,
        DateTimeOffset? ProcessingStartedAt,
        DateTimeOffset? CompletedAt,
        string? ResultSummary,
        string? ErrorMessage,
        long? TotalInputTokens,
        long? TotalOutputTokens,
        string? PrTitle = null,
        string? PrSourceBranch = null,
        string? PrTargetBranch = null,
        string? PrRepositoryName = null,
        string? AiModel = null,
        int FilesReviewed = 0,
        int? FilesInScope = null,
        decimal? TotalEstimatedCostUsd = null,
        bool CostIsApproximate = false,
        bool BudgetSoftCapped = false);

    /// <summary>Response for the job list endpoint.</summary>
    public sealed record JobListResponse(int Total, IReadOnlyList<JobListItem> Items);

    /// <summary>
    ///     Why a budget held or stopped a review: the binding scope, whether the soft or hard cap was reached, the
    ///     USD threshold, and the scope spend that reached it. Null when no budget blocked the job.
    /// </summary>
    public sealed record BudgetStatusDto(
        BudgetScopeKind Scope,
        BudgetCapKind CapKind,
        decimal ThresholdUsd,
        decimal SpentUsd);

    /// <summary>Detailed response for a single job, including the per-tier token breakdown.</summary>
    public sealed record JobDetailResponse(
        Guid Id,
        Guid ClientId,
        JobStatus Status,
        DateTimeOffset SubmittedAt,
        DateTimeOffset? ProcessingStartedAt,
        DateTimeOffset? CompletedAt,
        long? TotalInputTokens,
        long? TotalOutputTokens,
        string? ErrorMessage,
        string? AiModel,
        float? ReviewTemperature,
        IReadOnlyList<TokenBreakdownEntry> TokenBreakdown,
        bool? BreakdownConsistent,
        int FilesReviewed = 0,
        int? FilesInScope = null,
        decimal? TotalEstimatedCostUsd = null,
        bool CostIsApproximate = false,
        BudgetStatusDto? BudgetStatus = null);

    /// <summary>Response for the job result endpoint, combining status metadata with the review result.</summary>
    public sealed record ReviewJobResultDto(
        Guid JobId,
        Guid ClientId,
        JobStatus Status,
        DateTimeOffset SubmittedAt,
        DateTimeOffset? CompletedAt,
        ReviewResultDto Result);
}

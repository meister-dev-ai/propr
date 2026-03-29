using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>Provides a global view of all review jobs across all clients (admin only).</summary>
[ApiController]
[Route("jobs")]
public sealed class JobsController(IJobRepository jobRepository) : ControllerBase
{
    /// <summary>
    ///     Returns all review jobs across all clients, newest first. Requires <c>X-Admin-Key</c> or JWT Bearer token.
    /// </summary>
    /// <param name="limit">Maximum number of items to return (1–1000, default 100).</param>
    /// <param name="offset">Number of items to skip for pagination (default 0).</param>
    /// <param name="status">Optional status filter: Pending, Processing, Completed, or Failed.</param>
    /// <param name="clientId">Optional client filter: only return jobs for this client.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paginated list of all jobs.</returns>
    /// <response code="200">Jobs returned.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    [HttpGet]
    [ProducesResponseType(typeof(JobListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetAllJobs(
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        [FromQuery] JobStatus? status = null,
        [FromQuery] Guid? clientId = null,
        CancellationToken cancellationToken = default)
    {
        var isAdmin = this.HttpContext.Items["IsAdmin"] is true;
        var userId = this.HttpContext.Items["UserId"] is string s && Guid.TryParse(s, out var id) ? id : (Guid?)null;

        if (!isAdmin && userId is null)
        {
            return this.Unauthorized(new { error = "Valid credentials required." });
        }

        limit = Math.Clamp(limit, 1, 1000);
        offset = Math.Max(offset, 0);

        var (total, items) = await jobRepository.GetAllJobsAsync(limit, offset, status, clientId, cancellationToken);

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
                        j.Result?.Summary,
                        j.ErrorMessage,
                        j.TotalInputTokensAggregated ?? j.Protocols.Sum(p => (long?)p.TotalInputTokens),
                        j.TotalOutputTokensAggregated ?? j.Protocols.Sum(p => (long?)p.TotalOutputTokens),
                        j.PrTitle,
                        j.PrSourceBranch,
                        j.PrTargetBranch,
                        j.PrRepositoryName,
                        j.AiModel))
                    .ToList()));
    }

    /// <summary>Returns the review result (summary and comments) for a completed job. Requires <c>X-Admin-Key</c> or JWT Bearer token.</summary>
    /// <param name="id">The review job identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The review result, or 404 if the job has no result yet.</returns>
    /// <response code="200">Review result returned.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="404">Job not found or result not yet available.</response>
    [HttpGet("{id:guid}/result")]
    [ProducesResponseType(typeof(ReviewJobResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetJobResult(Guid id, CancellationToken cancellationToken = default)
    {
        var isAdmin = this.HttpContext.Items["IsAdmin"] is true;
        var userId = this.HttpContext.Items["UserId"] is string s && Guid.TryParse(s, out var uid) ? uid : (Guid?)null;

        if (!isAdmin && userId is null)
        {
            return this.Unauthorized(new { error = "Valid credentials required." });
        }

        var job = jobRepository.GetById(id);
        if (job is null)
        {
            return this.NotFound();
        }

        if (job.Result is null)
        {
            return this.NotFound();
        }

        return this.Ok(new ReviewJobResultDto(
            job.Id,
            job.ClientId,
            job.Status,
            job.SubmittedAt,
            job.CompletedAt,
            new ReviewResultDto(
                job.Result.Summary,
                job.Result.Comments
                    .Select(c => new ReviewCommentDto(c.FilePath, c.LineNumber, c.Severity, c.Message))
                    .ToArray())));
    }

    /// <summary>
    ///     Returns the protocol (agentic trace) for a single review job. Requires <c>X-Admin-Key</c> or JWT Bearer token.
    /// </summary>
    /// <param name="id">The review job identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The protocol records with all captured events, or 404 if the job has no protocols.</returns>
    /// <response code="200">Protocols returned.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="404">Job not found.</response>
    [HttpGet("{id:guid}/protocol")]
    [ProducesResponseType(typeof(IReadOnlyList<ReviewJobProtocolDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetJobProtocol(Guid id, CancellationToken cancellationToken = default)
    {
        var isAdmin = this.HttpContext.Items["IsAdmin"] is true;
        var userId = this.HttpContext.Items["UserId"] is string s && Guid.TryParse(s, out var uid) ? uid : (Guid?)null;

        if (!isAdmin && userId is null)
        {
            return this.Unauthorized(new { error = "Valid credentials required." });
        }

        var job = await jobRepository.GetByIdWithProtocolsAsync(id, cancellationToken);
        if (job is null)
        {
            return this.NotFound();
        }

        if (!job.Protocols.Any())
        {
            return this.NotFound();
        }

        var dtos = job.Protocols.Select(protocol => new ReviewJobProtocolDto(
                protocol.Id,
                protocol.JobId,
                protocol.AttemptNumber,
                protocol.Label,
                protocol.FileResultId,
                protocol.StartedAt,
                protocol.CompletedAt,
                protocol.Outcome,
                protocol.TotalInputTokens,
                protocol.TotalOutputTokens,
                protocol.IterationCount,
                protocol.ToolCallCount,
                protocol.FinalConfidence,
                protocol.Events.Select(e => new ProtocolEventDto(
                        e.Id,
                        e.Kind,
                        e.Name,
                        e.OccurredAt,
                        e.InputTokens,
                        e.OutputTokens,
                        e.InputTextSample,
                        e.OutputSummary,
                        e.Error))
                    .ToList()
                    .AsReadOnly()))
            .ToList();

        return this.Ok(dtos);
    }

    /// <summary>Single job item in the list response.</summary>
    public sealed record JobListItem(
        Guid Id,
        Guid? ClientId,
        string OrganizationUrl,
        string ProjectId,
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
        string? AiModel = null);

    /// <summary>Response for the job list endpoint.</summary>
    public sealed record JobListResponse(int Total, IReadOnlyList<JobListItem> Items);

    /// <summary>Response for the job result endpoint, combining status metadata with the review result.</summary>
    public sealed record ReviewJobResultDto(
        Guid JobId,
        Guid ClientId,
        JobStatus Status,
        DateTimeOffset SubmittedAt,
        DateTimeOffset? CompletedAt,
        ReviewResultDto Result);
}

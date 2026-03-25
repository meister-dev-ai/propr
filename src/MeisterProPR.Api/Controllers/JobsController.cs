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
    ///     Returns all review jobs across all clients, newest first. Requires <c>X-Admin-Key</c>.
    /// </summary>
    /// <param name="limit">Maximum number of items to return (1–1000, default 100).</param>
    /// <param name="offset">Number of items to skip for pagination (default 0).</param>
    /// <param name="status">Optional status filter: Pending, Processing, Completed, or Failed.</param>
    /// <param name="clientId">Optional client filter: only return jobs for this client.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paginated list of all jobs.</returns>
    /// <response code="200">Jobs returned.</response>
    /// <response code="401">Missing or invalid <c>X-Admin-Key</c> header.</response>
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
        if (this.HttpContext.Items["IsAdmin"] is not true)
        {
            return this.Unauthorized(new { error = "Valid X-Admin-Key required." });
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
                        j.Protocols.Sum(p => (long?)p.TotalInputTokens),
                        j.Protocols.Sum(p => (long?)p.TotalOutputTokens)))
                    .ToList()));
    }

    /// <summary>
    ///     Returns the protocol (agentic trace) for a single review job. Requires <c>X-Admin-Key</c>.
    /// </summary>
    /// <param name="id">The review job identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The protocol records with all captured events, or 404 if the job has no protocols.</returns>
    /// <response code="200">Protocols returned.</response>
    /// <response code="401">Missing or invalid <c>X-Admin-Key</c> header.</response>
    /// <response code="404">Job not found.</response>
    [HttpGet("{id:guid}/protocol")]
    [ProducesResponseType(typeof(IReadOnlyList<ReviewJobProtocolDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetJobProtocol(Guid id, CancellationToken cancellationToken = default)
    {
        if (this.HttpContext.Items["IsAdmin"] is not true)
        {
            return this.Unauthorized(new { error = "Valid X-Admin-Key required." });
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
        long? TotalOutputTokens);

    /// <summary>Response for the job list endpoint.</summary>
    public sealed record JobListResponse(int Total, IReadOnlyList<JobListItem> Items);
}

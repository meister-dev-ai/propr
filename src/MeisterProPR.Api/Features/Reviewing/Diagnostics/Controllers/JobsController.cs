// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Api.Extensions;
using MeisterProPR.Api.Features.Reviewing.Contracts;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Diagnostics.Queries.GetReviewJobProtocol;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>Provides a global view of all review jobs across all clients (admin only).</summary>
[ApiController]
public sealed class JobsController(
    IJobRepository jobRepository,
    IThreadMemoryRepository memoryRepository,
    GetReviewJobProtocolHandler getReviewJobProtocolHandler) : ControllerBase
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
    [HttpGet("/reviewing/jobs")]
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

        var (total, items) = await jobRepository.GetAllJobsAsync(
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
                        j.Result?.Summary,
                        j.ErrorMessage,
                        j.TotalInputTokensAggregated ?? j.Protocols.Sum(p => p.TotalInputTokens),
                        j.TotalOutputTokensAggregated ?? j.Protocols.Sum(p => p.TotalOutputTokens),
                        j.PrTitle,
                        j.PrSourceBranch,
                        j.PrTargetBranch,
                        j.PrRepositoryName,
                        j.AiModel))
                    .ToList()));
    }

    /// <summary>Returns detail for a single review job including per-tier token breakdown.</summary>
    /// <param name="id">The review job identifier.</param>
    /// <returns>Job detail with token breakdown, or 404 if not found.</returns>
    /// <response code="200">Job detail returned.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="404">Job not found.</response>
    [HttpGet("/reviewing/jobs/{id:guid}")]
    [HttpGet("/jobs/{id:guid}")]
    [ProducesResponseType(typeof(JobDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetJob(Guid id)
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
                breakdown,
                breakdownConsistent));
    }

    /// <summary>Returns the review result (summary and comments) for a completed job.</summary>
    /// <param name="id">The review job identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The review result, or 404 if the job has no result yet.</returns>
    /// <response code="200">Review result returned.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="404">Job not found or result not yet available.</response>
    [HttpGet("/reviewing/jobs/{id:guid}/result")]
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
                        .Select(c => new ReviewCommentDto(c.FilePath, c.LineNumber, c.Severity, c.Message))
                        .ToArray())));
    }

    /// <summary>
    ///     Returns the protocol (agentic trace) for a single review job.
    /// </summary>
    /// <param name="id">The review job identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The protocol records with all captured events, or 404 if the job has no protocols.</returns>
    /// <response code="200">Protocols returned.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="404">Job not found.</response>
    [HttpGet("/reviewing/jobs/{id:guid}/protocol")]
    [HttpGet("/jobs/{id:guid}/protocol")]
    [ProducesResponseType(typeof(IReadOnlyList<ReviewJobProtocolDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetJobProtocol(Guid id, CancellationToken cancellationToken = default)
    {
        var auth = AuthHelpers.RequireAuthenticated(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        var protocolResult = await getReviewJobProtocolHandler.HandleAsync(
            new GetReviewJobProtocolQuery(id),
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
    ///     Returns an aggregated view of all review jobs, token breakdowns, and memory records for a specific pull request.
    ///     Requires valid user authentication and access to the specified client.
    /// </summary>
    /// <param name="clientId">Owning client identifier.</param>
    /// <param name="providerScopePath">Provider scope path or host-qualified namespace for the repository.</param>
    /// <param name="providerProjectKey">Provider project, owner, or namespace key for the repository.</param>
    /// <param name="repositoryId">ADO repository identifier.</param>
    /// <param name="pullRequestId">Pull request number.</param>
    /// <param name="page">Page number (1-based, default 1).</param>
    /// <param name="pageSize">Page size (default 20, max 100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">PR view returned (empty DTO with zero jobs when the PR has no review jobs).</response>
    /// <response code="400">Missing or invalid parameters.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    [HttpGet("/clients/{clientId:guid}/reviewing/pr-view")]
    [HttpGet("/clients/{clientId:guid}/pr-view")]
    [ProducesResponseType(typeof(PrReviewViewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetPrView(
        Guid clientId,
        [FromQuery] string? providerScopePath,
        [FromQuery] string? providerProjectKey,
        [FromQuery] string? repositoryId,
        [FromQuery] int? pullRequestId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var auth = AuthHelpers.RequireAuthenticated(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        var roleCheck = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientUser);
        if (roleCheck is not null)
        {
            return roleCheck;
        }

        if (string.IsNullOrWhiteSpace(providerScopePath) ||
            string.IsNullOrWhiteSpace(providerProjectKey) ||
            string.IsNullOrWhiteSpace(repositoryId) ||
            pullRequestId is null or < 1)
        {
            return this.BadRequest(new { error = "providerScopePath, providerProjectKey, repositoryId and pullRequestId are required." });
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var jobs = await jobRepository.GetByPrAsync(
            clientId,
            providerScopePath,
            providerProjectKey,
            repositoryId,
            pullRequestId.Value,
            page,
            pageSize,
            cancellationToken);

        var breakdown = new List<TokenBreakdownEntry>();
        long totalInput = 0;
        long totalOutput = 0;
        foreach (var job in jobs)
        {
            totalInput += job.TotalInputTokensAggregated ?? job.Protocols.Sum(p => p.TotalInputTokens) ?? 0;
            totalOutput += job.TotalOutputTokensAggregated ?? job.Protocols.Sum(p => p.TotalOutputTokens) ?? 0;
            foreach (var entry in job.TokenBreakdown)
            {
                var existing = breakdown.Find(e =>
                    e.ConnectionCategory == entry.ConnectionCategory && e.ModelId == entry.ModelId);
                if (existing is not null)
                {
                    breakdown.Remove(existing);
                    breakdown.Add(
                        existing with
                        {
                            TotalInputTokens = existing.TotalInputTokens + entry.TotalInputTokens,
                            TotalOutputTokens = existing.TotalOutputTokens + entry.TotalOutputTokens,
                        });
                }
                else
                {
                    breakdown.Add(entry);
                }
            }
        }

        var breakdownInput = breakdown.Sum(e => e.TotalInputTokens);
        var breakdownOutput = breakdown.Sum(e => e.TotalOutputTokens);
        var breakdownConsistent = breakdown.Count == 0 ||
                                  (breakdownInput == totalInput && breakdownOutput == totalOutput);

        var originatedPaged = await memoryRepository.GetPagedAsync(
            clientId,
            null,
            1,
            50,
            MemorySource.ThreadResolved,
            repositoryId,
            pullRequestId.Value,
            cancellationToken);
        var originatedMemories = originatedPaged.Items
            .Select(r => new ThreadMemorySummaryDto(
                r.Id,
                r.ThreadId,
                r.FilePath,
                r.ResolutionSummary.Length > 200 ? r.ResolutionSummary[..200] : r.ResolutionSummary,
                r.MemorySource,
                r.UpdatedAt))
            .ToList()
            .AsReadOnly();

        var contributingMemoryIds = new HashSet<Guid>();
        foreach (var job in jobs)
        {
            foreach (var protocol in job.Protocols)
            {
                foreach (var ev in protocol.Events.Where(e =>
                             e.Name == "memory_reconsideration_completed" && e.InputTextSample != null))
                {
                    try
                    {
                        var doc = JsonDocument.Parse(ev.InputTextSample!);
                        if (doc.RootElement.TryGetProperty("contributingMemoryIds", out var idsEl))
                        {
                            foreach (var idEl in idsEl.EnumerateArray())
                            {
                                if (idEl.TryGetGuid(out var memId))
                                {
                                    contributingMemoryIds.Add(memId);
                                }
                            }
                        }
                    }
                    catch (JsonException)
                    {
                    }
                }
            }
        }

        var originatedIds = new HashSet<Guid>(originatedMemories.Select(m => m.MemoryRecordId));
        var externalContributingIds = contributingMemoryIds
            .Where(id => !originatedIds.Contains(id))
            .Take(50)
            .ToList();

        var contributingMemories = new List<ContributingMemorySummaryDto>();

        if (externalContributingIds.Count > 0)
        {
            var remainingIds = new HashSet<Guid>(externalContributingIds);
            var fetchPage = 1;
            const int fetchPageSize = 200;
            while (remainingIds.Count > 0)
            {
                var batch = await memoryRepository.GetPagedAsync(
                    clientId,
                    null,
                    fetchPage,
                    fetchPageSize,
                    ct: cancellationToken);
                foreach (var r in batch.Items)
                {
                    if (remainingIds.Remove(r.Id))
                    {
                        contributingMemories.Add(
                            new ContributingMemorySummaryDto(
                                r.Id,
                                r.MemorySource,
                                r.RepositoryId,
                                r.PullRequestId > 0 ? r.PullRequestId : null,
                                r.FilePath,
                                r.ResolutionSummary.Length > 200 ? r.ResolutionSummary[..200] : r.ResolutionSummary,
                                null));
                    }
                }

                if (fetchPage * fetchPageSize >= batch.TotalCount || remainingIds.Count == 0)
                {
                    break;
                }

                fetchPage++;
            }
        }

        var jobSummaries = jobs.Select(j => new PrJobSummaryDto(
                j.Id,
                j.Status,
                j.SubmittedAt,
                j.CompletedAt,
                j.Result?.Comments.Count,
                j.TotalInputTokensAggregated ?? j.Protocols.Sum(p => p.TotalInputTokens),
                j.TotalOutputTokensAggregated ?? j.Protocols.Sum(p => p.TotalOutputTokens),
                j.TokenBreakdown))
            .ToList()
            .AsReadOnly();

        var dto = new PrReviewViewDto(
            providerScopePath,
            providerProjectKey,
            repositoryId,
            pullRequestId.Value,
            jobs.Count,
            totalInput,
            totalOutput,
            breakdown.AsReadOnly(),
            breakdownConsistent,
            jobSummaries,
            originatedPaged.TotalCount,
            originatedMemories,
            externalContributingIds.Count,
            contributingMemories.AsReadOnly());

        return this.Ok(dto);
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
        string? AiModel = null);

    /// <summary>Response for the job list endpoint.</summary>
    public sealed record JobListResponse(int Total, IReadOnlyList<JobListItem> Items);

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
        IReadOnlyList<TokenBreakdownEntry> TokenBreakdown,
        bool? BreakdownConsistent);

    /// <summary>Response for the job result endpoint, combining status metadata with the review result.</summary>
    public sealed record ReviewJobResultDto(
        Guid JobId,
        Guid ClientId,
        JobStatus Status,
        DateTimeOffset SubmittedAt,
        DateTimeOffset? CompletedAt,
        ReviewResultDto Result);
}

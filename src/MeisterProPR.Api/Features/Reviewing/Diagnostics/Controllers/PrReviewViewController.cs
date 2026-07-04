// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Api.Extensions;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>Provides the aggregated, pull-request-scoped review view (jobs, token breakdowns, and memory records).</summary>
[ApiController]
[Route("clients/{clientId:guid}")]
public sealed class PrReviewViewController(
    IJobRepository jobRepository,
    IThreadMemoryRepository memoryRepository) : ControllerBase
{
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
    [HttpGet("reviewing/pr-view")]
    [HttpGet("pr-view")]
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
                        // The captured event body is a best-effort diagnostic sample; skip it if it isn't valid JSON.
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
                j.TokenBreakdown)
            {
                ResolvedReviewStrategy = j.ReviewStrategy,
            })
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
}

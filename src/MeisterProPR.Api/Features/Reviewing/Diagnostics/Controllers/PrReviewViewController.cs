// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Api.Extensions;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
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
    /// <param name="query">Query-string parameters identifying the PR and pagination window.</param>
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
        [FromQuery] GetPrViewQuery query,
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

        if (string.IsNullOrWhiteSpace(query.ProviderScopePath) ||
            string.IsNullOrWhiteSpace(query.ProviderProjectKey) ||
            string.IsNullOrWhiteSpace(query.RepositoryId) ||
            query.PullRequestId is null or < 1)
        {
            return this.BadRequest(new { error = "providerScopePath, providerProjectKey, repositoryId and pullRequestId are required." });
        }

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var jobs = await jobRepository.GetByPrAsync(
            clientId,
            query.ProviderScopePath,
            query.ProviderProjectKey,
            query.RepositoryId,
            query.PullRequestId.Value,
            page,
            pageSize,
            cancellationToken);

        var aggregation = AggregateTokenBreakdown(jobs);
        var breakdownConsistent = aggregation.Breakdown.Count == 0 ||
                                  (aggregation.BreakdownInput == aggregation.TotalInput
                                   && aggregation.BreakdownOutput == aggregation.TotalOutput);
        var costRollup = ComputeCostRollup(aggregation.Breakdown);

        var originatedPaged = await memoryRepository.GetPagedAsync(
            clientId,
            null,
            1,
            50,
            MemorySource.ThreadResolved,
            query.RepositoryId,
            query.PullRequestId.Value,
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

        var contributingMemoryIds = CollectContributingMemoryIds(jobs);
        var originatedIds = new HashSet<Guid>(originatedMemories.Select(m => m.MemoryRecordId));
        var externalContributingIds = contributingMemoryIds
            .Where(id => !originatedIds.Contains(id))
            .Take(50)
            .ToList();

        var contributingMemories = await ResolveContributingMemoriesAsync(
            memoryRepository,
            clientId,
            externalContributingIds,
            cancellationToken);

        var jobSummaries = jobs.Select(j => new PrJobSummaryDto(
                j.Id,
                j.Status,
                j.SubmittedAt,
                j.CompletedAt,
                j.Result?.Comments.Count,
                j.TotalInputTokensAggregated ?? j.Protocols.Sum(p => p.TotalInputTokens),
                j.TotalOutputTokensAggregated ?? j.Protocols.Sum(p => p.TotalOutputTokens),
                j.TokenBreakdown,
                j.TotalEstimatedCostUsd,
                j.CostIsApproximate))
            .ToList()
            .AsReadOnly();

        var dto = new PrReviewViewDto(
            query.ProviderScopePath,
            query.ProviderProjectKey,
            query.RepositoryId,
            query.PullRequestId.Value,
            jobs.Count,
            aggregation.TotalInput,
            aggregation.TotalOutput,
            aggregation.Breakdown.AsReadOnly(),
            breakdownConsistent,
            jobSummaries,
            originatedPaged.TotalCount,
            originatedMemories,
            externalContributingIds.Count,
            contributingMemories,
            costRollup.TotalEstimatedCostUsd,
            costRollup.CostIsApproximate);

        return this.Ok(dto);
    }

    private static TokenAggregation AggregateTokenBreakdown(IReadOnlyList<ReviewJob> jobs)
    {
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
                            EstimatedCostUsd = SumNullableCost(existing.EstimatedCostUsd, entry.EstimatedCostUsd),
                            CostIsApproximate = existing.CostIsApproximate || entry.CostIsApproximate
                                                                           || existing.EstimatedCostUsd.HasValue != entry.EstimatedCostUsd.HasValue,
                        });
                }
                else
                {
                    breakdown.Add(entry);
                }
            }
        }

        return new TokenAggregation(
            breakdown,
            totalInput,
            totalOutput,
            breakdown.Sum(e => e.TotalInputTokens),
            breakdown.Sum(e => e.TotalOutputTokens));
    }

    private static CostRollup ComputeCostRollup(IReadOnlyList<TokenBreakdownEntry> breakdown)
    {
        // Null-aware cost rollup: null when no tier is priced; approximate when any tier is
        // approximate or the PR mixes priced and unpriced tiers.
        var anyPricedTier = breakdown.Any(e => e.EstimatedCostUsd.HasValue);
        var anyUnpricedTier = breakdown.Any(e => !e.EstimatedCostUsd.HasValue);
        var totalEstimatedCostUsd = anyPricedTier
            ? breakdown.Sum(e => e.EstimatedCostUsd ?? 0m)
            : (decimal?)null;
        var costIsApproximate = breakdown.Any(e => e.CostIsApproximate) || (anyPricedTier && anyUnpricedTier);
        return new CostRollup(totalEstimatedCostUsd, costIsApproximate);
    }

    private static HashSet<Guid> CollectContributingMemoryIds(IReadOnlyList<ReviewJob> jobs)
    {
        var contributingMemoryIds = new HashSet<Guid>();
        foreach (var job in jobs)
        {
            foreach (var protocol in job.Protocols)
            {
                foreach (var ev in protocol.Events.Where(e =>
                             e.Name == ReviewProtocolEventNames.MemoryReconsiderationCompleted && e.InputTextSample != null))
                {
                    AddContributingMemoryIdsFromEvent(ev.InputTextSample!, contributingMemoryIds);
                }
            }
        }

        return contributingMemoryIds;
    }

    private static void AddContributingMemoryIdsFromEvent(string inputTextSample, HashSet<Guid> sink)
    {
        try
        {
            var doc = JsonDocument.Parse(inputTextSample);
            if (!doc.RootElement.TryGetProperty("contributingMemoryIds", out var idsEl))
            {
                return;
            }

            foreach (var idEl in idsEl.EnumerateArray())
            {
                if (idEl.TryGetGuid(out var memId))
                {
                    sink.Add(memId);
                }
            }
        }
        catch (JsonException)
        {
            // The captured event body is a best-effort diagnostic sample; skip it if it isn't valid JSON.
        }
    }

    private static async Task<IReadOnlyList<ContributingMemorySummaryDto>> ResolveContributingMemoriesAsync(
        IThreadMemoryRepository memoryRepository,
        Guid clientId,
        IReadOnlyList<Guid> externalContributingIds,
        CancellationToken cancellationToken)
    {
        if (externalContributingIds.Count == 0)
        {
            return [];
        }

        var remainingIds = new HashSet<Guid>(externalContributingIds);
        var contributingMemories = new List<ContributingMemorySummaryDto>();
        var fetchPage = 1;
        const int fetchPageSize = 200;

        // The contributing-memory IDs come from request-scoped protocol JSON, so without a bound a
        // high-cardinality set could page through the entire client-wide memory corpus in a single
        // request. Cap the pages scanned; any IDs unresolved within this window are omitted from the
        // detail list (the count reported above stays accurate.
        const int maxPagesToScan = 25;
        while (remainingIds.Count > 0 && fetchPage <= maxPagesToScan)
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

        return contributingMemories;
    }

    private static decimal? SumNullableCost(decimal? left, decimal? right)
    {
        if (left is null && right is null)
        {
            return null;
        }

        return (left ?? 0m) + (right ?? 0m);
    }

    private sealed record TokenAggregation(
        List<TokenBreakdownEntry> Breakdown,
        long TotalInput,
        long TotalOutput,
        long BreakdownInput,
        long BreakdownOutput);

    private sealed record CostRollup(decimal? TotalEstimatedCostUsd, bool CostIsApproximate);
}

/// <summary>Query string for <see cref="PrReviewViewController.GetPrView" />.</summary>
public sealed record GetPrViewQuery(
    [property: FromQuery(Name = "providerScopePath")]
    string? ProviderScopePath,
    [property: FromQuery(Name = "providerProjectKey")]
    string? ProviderProjectKey,
    [property: FromQuery(Name = "repositoryId")]
    string? RepositoryId,
    [property: FromQuery(Name = "pullRequestId")]
    int? PullRequestId,
    [property: FromQuery(Name = "page")] int Page = 1,
    [property: FromQuery(Name = "pageSize")]
    int PageSize = 20);

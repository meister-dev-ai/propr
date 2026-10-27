// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Api.Extensions;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Diagnostics.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.AspNetCore.Mvc;
using MeisterDev.ProPR.Web;

namespace MeisterDev.ProPR.Api.Controllers;

/// <summary>Provides the aggregated, pull-request-scoped review view (jobs, token breakdowns, and memory records).</summary>
[ApiController]
[Route("clients/{clientId:guid}")]
public sealed class PrReviewViewController(
    IPrReviewViewReader viewReader,
    IThreadMemoryRepository memoryRepository) : ControllerBase
{
    /// <summary>How much of a stored resolution summary the PR view renders.</summary>
    private const int ResolutionSummaryExcerptLength = 200;

    /// <summary>Most memories originating in this pull request that the view lists.</summary>
    private const int OriginatedMemoryLimit = 50;

    /// <summary>Most external contributing memories the view resolves for display.</summary>
    private const int ContributingMemoryDetailLimit = 50;

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
    [TenantData(TenantDataAccess.Content, Scope = ContentScopeSource.ClientRoute, Resource = "the pull request view")]
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

        var viewData = await viewReader.ReadAsync(
            clientId,
            query.ProviderScopePath,
            query.ProviderProjectKey,
            query.RepositoryId,
            query.PullRequestId.Value,
            page,
            pageSize,
            cancellationToken);
        var jobs = viewData.Jobs;

        var aggregation = AggregateTokenBreakdown(jobs);
        var breakdownConsistent = aggregation.Breakdown.Count == 0 ||
                                  (aggregation.BreakdownInput == aggregation.TotalInput
                                   && aggregation.BreakdownOutput == aggregation.TotalOutput);
        var costRollup = ComputeCostRollup(aggregation.Breakdown);

        var originatedDigests = await memoryRepository.GetDigestsForPullRequestAsync(
            clientId,
            query.ProviderScopePath,
            query.ProviderProjectKey,
            query.RepositoryId,
            query.PullRequestId.Value,
            MemorySource.ThreadResolved,
            OriginatedMemoryLimit,
            cancellationToken);
        var originatedMemories = originatedDigests.Items
            .Select(r => new ThreadMemorySummaryDto(
                r.Id,
                r.ThreadId,
                r.FilePath,
                Excerpt(r.ResolutionSummary),
                r.MemorySource,
                r.UpdatedAt,
                r.ResolutionIntent,
                r.ResolutionClarity))
            .ToList()
            .AsReadOnly();

        var contributingMemoryIds = CollectContributingMemoryIds(viewData.ContributingMemoryPayloads);
        var originatedIds = new HashSet<Guid>(originatedMemories.Select(m => m.MemoryRecordId));
        var externalContributingIds = contributingMemoryIds
            .Where(id => !originatedIds.Contains(id))
            .Take(ContributingMemoryDetailLimit)
            .ToList();

        var contributingMemories = await ResolveContributingMemoriesAsync(
            memoryRepository,
            clientId,
            externalContributingIds,
            cancellationToken);

        var threadPassCost = ComputeThreadPassCostRollup(viewData.ThreadPasses);
        var pendingReview = ResolvePendingReview(viewData.Watermark);

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
            jobs,
            originatedDigests.TotalCount,
            originatedMemories,
            externalContributingIds.Count,
            contributingMemories,
            costRollup.TotalEstimatedCostUsd,
            costRollup.CostIsApproximate,
            viewData.ThreadPasses,
            threadPassCost.TotalEstimatedCostUsd,
            threadPassCost.CostIsApproximate,
            pendingReview);

        return this.Ok(dto);
    }

    /// <summary>
    ///     Returns pending revision metadata when the revision differs from the last reviewed revision.
    ///     The UI and browser extension use the same comparison to decide whether to offer a review.
    /// </summary>
    /// <remarks>
    ///     Reviewing a pending revision updates the reviewed watermark. Equal revision keys indicate that
    ///     no pending review remains, without requiring another write to clear the pending watermark.
    /// </remarks>
    private static PendingReviewDto? ResolvePendingReview(PrReviewWatermark? scan)
    {
        if (scan is null || string.IsNullOrEmpty(scan.PendingReviewRevisionKey))
        {
            return null;
        }

        if (string.Equals(scan.PendingReviewRevisionKey, scan.LastProcessedCommitId, StringComparison.Ordinal))
        {
            return null;
        }

        return new PendingReviewDto(
            scan.PendingReviewRevisionKey,
            string.IsNullOrEmpty(scan.LastProcessedCommitId) ? null : scan.LastProcessedCommitId,
            scan.PendingReviewDetectedAt);
    }

    private static TokenAggregation AggregateTokenBreakdown(IReadOnlyList<PrJobSummaryDto> jobs)
    {
        var breakdown = new List<TokenBreakdownEntry>();
        long totalInput = 0;
        long totalOutput = 0;
        foreach (var job in jobs)
        {
            totalInput += job.TotalInputTokens ?? 0;
            totalOutput += job.TotalOutputTokens ?? 0;
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

    private static CostRollup ComputeThreadPassCostRollup(IReadOnlyList<PrThreadPassSummaryDto> passes)
    {
        // Null when nothing priced was recorded, so an operator reads "not known" rather than "free".
        var anyPriced = passes.Any(pass => pass.TotalEstimatedCostUsd.HasValue);
        return new CostRollup(
            anyPriced ? passes.Sum(pass => pass.TotalEstimatedCostUsd ?? 0m) : null,
            passes.Any(pass => pass.CostIsApproximate));
    }

    private static HashSet<Guid> CollectContributingMemoryIds(IReadOnlyList<string> payloads)
    {
        var contributingMemoryIds = new HashSet<Guid>();
        foreach (var payload in payloads)
        {
            AddContributingMemoryIdsFromEvent(payload, contributingMemoryIds);
        }

        return contributingMemoryIds;
    }

    private static void AddContributingMemoryIdsFromEvent(string inputTextSample, HashSet<Guid> sink)
    {
        try
        {
            using var doc = JsonDocument.Parse(inputTextSample);
            if (!doc.RootElement.TryGetProperty("contributingMemoryIds", out var idsEl))
            {
                return;
            }

            // Shape is checked rather than assumed. Enumerating a scalar, object or null throws
            // InvalidOperationException rather than JsonException, so the catch below would not hold it and one
            // malformed persisted event would fail the whole pull-request view instead of being skipped.
            if (idsEl.ValueKind != JsonValueKind.Array)
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

        // These are record identifiers, so one keyed lookup resolves every one of them regardless of
        // how large the client's memory corpus is, and returns no result for ids that do not belong
        // to this client. Ids with no surviving record are simply absent; the count reported
        // alongside this list stays accurate either way.
        var digests = await memoryRepository.GetDigestsByIdsAsync(
            clientId,
            externalContributingIds,
            cancellationToken);

        return digests
            .Select(r => new ContributingMemorySummaryDto(
                r.Id,
                r.MemorySource,
                r.RepositoryId,
                r.PullRequestId > 0 ? r.PullRequestId : null,
                r.FilePath,
                Excerpt(r.ResolutionSummary),
                null))
            .ToList();
    }

    /// <summary>Caps a stored resolution summary to the length the read surfaces display.</summary>
    private static string Excerpt(string resolutionSummary)
    {
        return resolutionSummary.Length > ResolutionSummaryExcerptLength
            ? resolutionSummary[..ResolutionSummaryExcerptLength]
            : resolutionSummary;
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

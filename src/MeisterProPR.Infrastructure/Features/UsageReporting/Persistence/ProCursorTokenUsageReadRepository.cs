// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>
///     EF Core read repository for ProCursor token usage reporting.
/// </summary>
public sealed class ProCursorTokenUsageReadRepository(MeisterProPRDbContext db) : IProCursorTokenUsageReadRepository
{
    public async Task<ProCursorTokenUsageResponse> GetClientUsageAsync(
        Guid clientId,
        DateOnly from,
        DateOnly to,
        ProCursorTokenUsageGranularity granularity,
        string? groupBy,
        CancellationToken ct = default)
    {
        var events = await this.QueryEvents(clientId, from, to, null)
            .OrderBy(item => item.OccurredAtUtc)
            .ToListAsync(ct);

        var totals = BuildTotals(events);
        var normalizedGroupBy = NormalizeGroupBy(groupBy);
        var series = events
            .GroupBy(item => GetBucketStart(item.OccurredAtUtc, granularity))
            .OrderBy(group => group.Key)
            .Select(group => new ProCursorTokenUsageSeriesPointDto(
                group.Key,
                group.Sum(item => item.PromptTokens),
                group.Sum(item => item.CompletionTokens),
                group.Sum(item => item.TotalTokens),
                SumCosts(group),
                BuildBreakdown(group, normalizedGroupBy)))
            .ToList()
            .AsReadOnly();

        var topSources = BuildTopSources(events, 10);
        var freshness = await this.GetFreshnessAsync(clientId, ct);

        return new ProCursorTokenUsageResponse(
            clientId,
            from,
            to,
            granularity,
            normalizedGroupBy,
            totals,
            series,
            topSources,
            IncludesGapFilledEvents: freshness.LastRollupCompletedAtUtc is null || DateOnly.FromDateTime(freshness.LastRollupCompletedAtUtc.Value.UtcDateTime) < to,
            IncludesEstimatedUsage: totals.EstimatedEventCount > 0,
            LastRollupCompletedAtUtc: freshness.LastRollupCompletedAtUtc);
    }

    public async Task<ProCursorSourceTokenUsageResponse?> GetSourceUsageAsync(
        Guid clientId,
        Guid sourceId,
        DateOnly from,
        DateOnly to,
        ProCursorTokenUsageGranularity granularity,
        CancellationToken ct = default)
    {
        var source = await db.ProCursorKnowledgeSources
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.ClientId == clientId && candidate.Id == sourceId, ct);
        if (source is null)
        {
            return null;
        }

        var events = await this.QueryEvents(clientId, from, to, sourceId)
            .OrderBy(item => item.OccurredAtUtc)
            .ToListAsync(ct);

        var totals = BuildTotals(events);
        var series = events
            .GroupBy(item => GetBucketStart(item.OccurredAtUtc, granularity))
            .OrderBy(group => group.Key)
            .Select(group => new ProCursorTokenUsageSeriesPointDto(
                group.Key,
                group.Sum(item => item.PromptTokens),
                group.Sum(item => item.CompletionTokens),
                group.Sum(item => item.TotalTokens),
                SumCosts(group),
                BuildBreakdown(group, "model")))
            .ToList()
            .AsReadOnly();

        var byModel = events
            .GroupBy(item => item.ModelName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ProCursorSourceModelUsageDto(
                group.Key,
                group.Sum(item => item.PromptTokens),
                group.Sum(item => item.CompletionTokens),
                group.Sum(item => item.TotalTokens),
                SumCosts(group),
                group.LongCount(),
                group.LongCount(item => item.TokensEstimated)))
            .ToList()
            .AsReadOnly();

        var freshness = await this.GetFreshnessAsync(clientId, ct);

        return new ProCursorSourceTokenUsageResponse(
            clientId,
            sourceId,
            source.DisplayName,
            from,
            to,
            granularity,
            totals,
            byModel,
            series,
            $"/admin/clients/{clientId}/procursor/sources/{sourceId}/token-usage/events?limit=50",
            IncludesGapFilledEvents: freshness.LastRollupCompletedAtUtc is null || DateOnly.FromDateTime(freshness.LastRollupCompletedAtUtc.Value.UtcDateTime) < to,
            IncludesEstimatedUsage: totals.EstimatedEventCount > 0,
            LastRollupCompletedAtUtc: freshness.LastRollupCompletedAtUtc);
    }

    public async Task<IReadOnlyList<ProCursorTopSourceUsageDto>> GetTopSourcesAsync(
        Guid clientId,
        DateOnly from,
        DateOnly to,
        int limit,
        CancellationToken ct = default)
    {
        var events = await this.QueryEvents(clientId, from, to, null)
            .ToListAsync(ct);

        return BuildTopSources(events, limit);
    }

    public async Task<ProCursorTokenUsageEventsResponse?> GetRecentEventsAsync(
        Guid clientId,
        Guid sourceId,
        int limit,
        CancellationToken ct = default)
    {
        var sourceExists = await db.ProCursorKnowledgeSources
            .AsNoTracking()
            .AnyAsync(candidate => candidate.ClientId == clientId && candidate.Id == sourceId, ct);
        if (!sourceExists)
        {
            return null;
        }

        if (limit <= 0)
        {
            return new ProCursorTokenUsageEventsResponse(clientId, sourceId, Array.Empty<ProCursorTokenUsageEventDto>());
        }

        var items = await db.ProCursorTokenUsageEvents
            .AsNoTracking()
            .Where(item => item.ClientId == clientId && item.ProCursorSourceId == sourceId)
            .OrderByDescending(item => item.OccurredAtUtc)
            .Take(limit)
            .Select(item => new ProCursorTokenUsageEventDto(
                item.OccurredAtUtc,
                item.RequestId,
                item.CallType,
                item.ModelName,
                item.DeploymentName,
                item.PromptTokens,
                item.CompletionTokens,
                item.TotalTokens,
                item.EstimatedCostUsd,
                item.TokensEstimated,
                item.CostEstimated,
                item.IndexJobId,
                item.SourcePath,
                item.ResourceId,
                item.KnowledgeChunkId))
            .ToListAsync(ct);

        return new ProCursorTokenUsageEventsResponse(clientId, sourceId, items.AsReadOnly());
    }

    public async Task<IReadOnlyList<ProCursorTokenUsageExportRowDto>> ExportAsync(
        Guid clientId,
        DateOnly from,
        DateOnly to,
        Guid? sourceId,
        CancellationToken ct = default)
    {
        var rows = await this.QueryEvents(clientId, from, to, sourceId)
            .OrderBy(item => item.OccurredAtUtc)
            .Select(item => new ProCursorTokenUsageExportRowDto(
                DateOnly.FromDateTime(item.OccurredAtUtc.UtcDateTime),
                item.ProCursorSourceId,
                item.SourceDisplayNameSnapshot,
                item.ModelName,
                item.CallType,
                item.PromptTokens,
                item.CompletionTokens,
                item.TotalTokens,
                item.EstimatedCostUsd,
                item.TokensEstimated,
                item.IndexJobId,
                item.SourcePath,
                item.ResourceId,
                item.KnowledgeChunkId))
            .ToListAsync(ct);

        return rows.AsReadOnly();
    }

    public async Task<ProCursorTokenUsageFreshnessResponse> GetFreshnessAsync(Guid clientId, CancellationToken ct = default)
    {
        var lastRollup = await db.ProCursorTokenUsageRollups
            .AsNoTracking()
            .Where(item => item.ClientId == clientId)
            .MaxAsync(item => (DateTimeOffset?)item.LastRecomputedAtUtc, ct);

        return new ProCursorTokenUsageFreshnessResponse(
            clientId,
            lastRollup,
            null,
            lastRollup is null);
    }

    private IQueryable<ProCursorTokenUsageEvent> QueryEvents(
        Guid clientId,
        DateOnly from,
        DateOnly to,
        Guid? sourceId)
    {
        var start = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var endExclusive = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var query = db.ProCursorTokenUsageEvents
            .AsNoTracking()
            .Where(item => item.ClientId == clientId && item.OccurredAtUtc >= start && item.OccurredAtUtc < endExclusive);

        if (sourceId.HasValue)
        {
            query = query.Where(item => item.ProCursorSourceId == sourceId.Value);
        }

        return query;
    }

    private static IReadOnlyList<ProCursorTopSourceUsageDto> BuildTopSources(IEnumerable<ProCursorTokenUsageEvent> events, int limit)
    {
        if (limit <= 0)
        {
            return Array.Empty<ProCursorTopSourceUsageDto>();
        }

        return events
            .GroupBy(
                item => new { item.ProCursorSourceId, item.SourceDisplayNameSnapshot },
                item => item)
            .OrderByDescending(group => group.Sum(item => item.TotalTokens))
            .ThenBy(group => group.Key.SourceDisplayNameSnapshot, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select((group, index) => new ProCursorTopSourceUsageDto(
                index + 1,
                group.Key.ProCursorSourceId,
                group.Key.SourceDisplayNameSnapshot,
                group.Sum(item => item.TotalTokens),
                SumCosts(group),
                group.LongCount(item => item.TokensEstimated)))
            .ToList()
            .AsReadOnly();
    }

    private static ProCursorTokenUsageTotalsDto BuildTotals(IEnumerable<ProCursorTokenUsageEvent> events)
    {
        var eventList = events as IReadOnlyCollection<ProCursorTokenUsageEvent> ?? events.ToList();
        return new ProCursorTokenUsageTotalsDto(
            eventList.Sum(item => item.PromptTokens),
            eventList.Sum(item => item.CompletionTokens),
            eventList.Sum(item => item.TotalTokens),
            SumCosts(eventList),
            eventList.LongCount(),
            eventList.LongCount(item => item.TokensEstimated));
    }

    private static IReadOnlyList<ProCursorTokenUsageBreakdownItemDto> BuildBreakdown(
        IEnumerable<ProCursorTokenUsageEvent> events,
        string normalizedGroupBy)
    {
        if (string.Equals(normalizedGroupBy, "model", StringComparison.OrdinalIgnoreCase))
        {
            return events
                .GroupBy(item => item.ModelName, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => new ProCursorTokenUsageBreakdownItemDto(
                    null,
                    null,
                    group.Key,
                    group.Sum(item => item.PromptTokens),
                    group.Sum(item => item.CompletionTokens),
                    group.Sum(item => item.TotalTokens),
                    SumCosts(group),
                    group.Any(item => item.TokensEstimated),
                    group.LongCount(),
                    group.LongCount(item => item.TokensEstimated)))
                .ToList()
                .AsReadOnly();
        }

        return events
            .GroupBy(item => new { item.ProCursorSourceId, item.SourceDisplayNameSnapshot, item.ModelName })
            .OrderBy(group => group.Key.SourceDisplayNameSnapshot, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key.ModelName, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ProCursorTokenUsageBreakdownItemDto(
                group.Key.ProCursorSourceId,
                group.Key.SourceDisplayNameSnapshot,
                group.Key.ModelName,
                group.Sum(item => item.PromptTokens),
                group.Sum(item => item.CompletionTokens),
                group.Sum(item => item.TotalTokens),
                SumCosts(group),
                group.Any(item => item.TokensEstimated),
                group.LongCount(),
                group.LongCount(item => item.TokensEstimated)))
            .ToList()
            .AsReadOnly();
    }

    private static decimal? SumCosts(IEnumerable<ProCursorTokenUsageEvent> events)
    {
        var items = events.Where(item => item.EstimatedCostUsd.HasValue).Select(item => item.EstimatedCostUsd!.Value).ToList();
        return items.Count == 0 ? null : items.Sum();
    }

    private static DateOnly GetBucketStart(DateTimeOffset occurredAtUtc, ProCursorTokenUsageGranularity granularity)
    {
        var date = DateOnly.FromDateTime(occurredAtUtc.UtcDateTime);
        return granularity == ProCursorTokenUsageGranularity.Monthly
            ? new DateOnly(date.Year, date.Month, 1)
            : date;
    }

    private static string NormalizeGroupBy(string? groupBy)
    {
        return string.Equals(groupBy, "model", StringComparison.OrdinalIgnoreCase)
            ? "model"
            : "source";
    }
}

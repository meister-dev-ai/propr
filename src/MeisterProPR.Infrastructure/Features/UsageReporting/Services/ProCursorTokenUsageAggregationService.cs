// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Services;

/// <summary>
///     Recomputes ProCursor token usage rollups from raw event history.
/// </summary>
public sealed class ProCursorTokenUsageAggregationService(MeisterProPRDbContext db)
    : IProCursorTokenUsageAggregationService
{
    public Task<int> RefreshRecentAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return this.RefreshAsync(today.AddDays(-1), today, null, true, ct);
    }

    public async Task<int> RefreshAsync(
        DateOnly from,
        DateOnly to,
        Guid? clientId = null,
        bool includeMonthly = true,
        CancellationToken ct = default)
    {
        if (to < from)
        {
            throw new ArgumentException("to must be greater than or equal to from.", nameof(to));
        }

        var now = DateTimeOffset.UtcNow;
        var rollups = new List<ProCursorTokenUsageRollup>();
        var dailyEvents = await this.QueryEventsAsync(from, to, clientId, ct);

        rollups.AddRange(BuildRollups(dailyEvents, ProCursorTokenUsageGranularity.Daily, now));

        var dailyExisting = await db.ProCursorTokenUsageRollups
            .Where(item => item.BucketStartDate >= from && item.BucketStartDate <= to)
            .Where(item => !clientId.HasValue || item.ClientId == clientId.Value)
            .Where(item => item.Granularity == ProCursorTokenUsageGranularity.Daily)
            .ToListAsync(ct);

        if (dailyExisting.Count > 0)
        {
            db.ProCursorTokenUsageRollups.RemoveRange(dailyExisting);
        }

        if (includeMonthly)
        {
            var monthStart = new DateOnly(from.Year, from.Month, 1);
            var monthEnd = new DateOnly(to.Year, to.Month, 1);
            var monthlyEvents = await this.QueryEventsAsync(
                monthStart,
                ToMonthEnd(monthEnd),
                clientId,
                ct);

            rollups.AddRange(BuildRollups(monthlyEvents, ProCursorTokenUsageGranularity.Monthly, now));

            var monthlyExisting = await db.ProCursorTokenUsageRollups
                .Where(item => item.BucketStartDate >= monthStart && item.BucketStartDate <= monthEnd)
                .Where(item => !clientId.HasValue || item.ClientId == clientId.Value)
                .Where(item => item.Granularity == ProCursorTokenUsageGranularity.Monthly)
                .ToListAsync(ct);

            if (monthlyExisting.Count > 0)
            {
                db.ProCursorTokenUsageRollups.RemoveRange(monthlyExisting);
            }
        }

        if (rollups.Count > 0)
        {
            db.ProCursorTokenUsageRollups.AddRange(rollups);
        }

        await db.SaveChangesAsync(ct);
        return rollups.Count;
    }

    private async Task<List<ProCursorTokenUsageEvent>> QueryEventsAsync(
        DateOnly from,
        DateOnly to,
        Guid? clientId,
        CancellationToken ct)
    {
        var start = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var endExclusive = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var query = db.ProCursorTokenUsageEvents
            .AsNoTracking()
            .Where(item => item.OccurredAtUtc >= start && item.OccurredAtUtc < endExclusive);

        if (clientId.HasValue)
        {
            query = query.Where(item => item.ClientId == clientId.Value);
        }

        return await query.ToListAsync(ct);
    }

    private static IEnumerable<ProCursorTokenUsageRollup> BuildRollups(
        IEnumerable<ProCursorTokenUsageEvent> events,
        ProCursorTokenUsageGranularity granularity,
        DateTimeOffset recomputedAtUtc)
    {
        foreach (var group in GroupEvents(events, granularity, true))
        {
            yield return BuildRollup(group, granularity, recomputedAtUtc, true);
        }

        foreach (var group in GroupEvents(events, granularity, false))
        {
            yield return BuildRollup(group, granularity, recomputedAtUtc, false);
        }
    }

    private static
        IEnumerable<IGrouping<(Guid ClientId, Guid? SourceId, string? SourceDisplayName, DateOnly BucketStart, string
            ModelName), ProCursorTokenUsageEvent>> GroupEvents(
            IEnumerable<ProCursorTokenUsageEvent> events,
            ProCursorTokenUsageGranularity granularity,
            bool includeSource)
    {
        return events.GroupBy(item => (
            item.ClientId,
            includeSource ? item.ProCursorSourceId : (Guid?)null,
            includeSource ? item.SourceDisplayNameSnapshot : null,
            GetBucketStart(item.OccurredAtUtc, granularity),
            item.ModelName));
    }

    private static ProCursorTokenUsageRollup BuildRollup(
        IGrouping<(Guid ClientId, Guid? SourceId, string? SourceDisplayName, DateOnly BucketStart, string ModelName),
            ProCursorTokenUsageEvent> group,
        ProCursorTokenUsageGranularity granularity,
        DateTimeOffset recomputedAtUtc,
        bool includeSource)
    {
        var estimatedCosts = group.Where(item => item.EstimatedCostUsd.HasValue)
            .Select(item => item.EstimatedCostUsd!.Value)
            .ToList();
        return new ProCursorTokenUsageRollup(
            Guid.NewGuid(),
            group.Key.ClientId,
            includeSource ? group.Key.SourceId : null,
            includeSource ? group.Key.SourceDisplayName : null,
            group.Key.BucketStart,
            granularity,
            group.Key.ModelName,
            group.Sum(item => item.PromptTokens),
            group.Sum(item => item.CompletionTokens),
            estimatedCosts.Count == 0 ? null : estimatedCosts.Sum(),
            group.LongCount(),
            group.LongCount(item => item.TokensEstimated),
            recomputedAtUtc);
    }

    private static DateOnly GetBucketStart(DateTimeOffset occurredAtUtc, ProCursorTokenUsageGranularity granularity)
    {
        var date = DateOnly.FromDateTime(occurredAtUtc.UtcDateTime);
        return granularity == ProCursorTokenUsageGranularity.Monthly
            ? new DateOnly(date.Year, date.Month, 1)
            : date;
    }

    private static DateOnly ToMonthEnd(DateOnly monthStart)
    {
        return monthStart.AddMonths(1).AddDays(-1);
    }
}

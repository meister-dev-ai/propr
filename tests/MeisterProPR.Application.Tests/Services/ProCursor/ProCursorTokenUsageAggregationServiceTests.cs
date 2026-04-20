// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Application.Tests.Services.ProCursor;

public sealed class ProCursorTokenUsageAggregationServiceTests
{
    private static MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new MeisterProPRDbContext(options);
    }

    [Fact]
    public async Task RefreshAsync_RebuildsDailyAndMonthlyRollupsFromEventHistory()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();

        db.ProCursorTokenUsageEvents.AddRange(
            CreateEvent(
                clientId,
                sourceId,
                new DateTimeOffset(2026, 4, 4, 8, 0, 0, TimeSpan.Zero),
                120,
                0,
                0.00012m,
                false),
            CreateEvent(
                clientId,
                sourceId,
                new DateTimeOffset(2026, 4, 5, 9, 0, 0, TimeSpan.Zero),
                80,
                40,
                0.0002m,
                true));
        db.ProCursorTokenUsageRollups.Add(
            new ProCursorTokenUsageRollup(
                Guid.NewGuid(),
                clientId,
                sourceId,
                "Platform Wiki",
                new DateOnly(2026, 4, 4),
                ProCursorTokenUsageGranularity.Daily,
                "text-embedding-3-small",
                1,
                0,
                0.00001m,
                1,
                0,
                new DateTimeOffset(2026, 4, 4, 12, 0, 0, TimeSpan.Zero)));
        await db.SaveChangesAsync();

        var service = new ProCursorTokenUsageAggregationService(db);

        var rebuiltCount = await service.RefreshAsync(
            new DateOnly(2026, 4, 4),
            new DateOnly(2026, 4, 5),
            clientId);

        Assert.Equal(6, rebuiltCount);

        var dailyRollups = await db.ProCursorTokenUsageRollups
            .Where(item => item.ClientId == clientId && item.Granularity == ProCursorTokenUsageGranularity.Daily &&
                           item.ProCursorSourceId == sourceId)
            .OrderBy(item => item.BucketStartDate)
            .ToListAsync();
        Assert.Equal(2, dailyRollups.Count);
        Assert.Equal(120, dailyRollups[0].TotalTokens);
        Assert.Equal(120, dailyRollups[1].TotalTokens);
        Assert.Equal(1, dailyRollups[1].EstimatedEventCount);

        var monthlyRollup = await db.ProCursorTokenUsageRollups.SingleAsync(item => item.ClientId == clientId
                                                                                    && item.Granularity == ProCursorTokenUsageGranularity.Monthly
                                                                                    && item.ProCursorSourceId == sourceId);
        Assert.Equal(new DateOnly(2026, 4, 1), monthlyRollup.BucketStartDate);
        Assert.Equal(240, monthlyRollup.TotalTokens);
        Assert.Equal(2, monthlyRollup.EventCount);
        Assert.Equal(1, monthlyRollup.EstimatedEventCount);
    }

    private static ProCursorTokenUsageEvent CreateEvent(
        Guid clientId,
        Guid sourceId,
        DateTimeOffset occurredAtUtc,
        long promptTokens,
        long completionTokens,
        decimal estimatedCostUsd,
        bool estimated)
    {
        return new ProCursorTokenUsageEvent(
            Guid.NewGuid(),
            clientId,
            sourceId,
            "Platform Wiki",
            $"pcidx:test:{occurredAtUtc:yyyyMMddHHmmssfff}:{Guid.NewGuid():N}",
            occurredAtUtc,
            ProCursorTokenUsageCallType.Embedding,
            "text-embedding-3-small",
            "text-embedding-3-small",
            "cl100k_base",
            promptTokens,
            completionTokens,
            estimated,
            estimatedCostUsd,
            true);
    }
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Repositories;
using MeisterProPR.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Application.Tests.Features.UsageReporting;

public sealed class UsageReportingModuleTests
{
    [Fact]
    public async Task UpsertAsync_WhenSampleExists_AccumulatesTokenCounts()
    {
        await using var db = CreateContext();
        var repository = new ClientTokenUsageRepository(db);
        var clientId = Guid.NewGuid();
        var date = new DateOnly(2026, 4, 6);

        await repository.UpsertAsync(clientId, "gpt-4o", date, 100, 25, CancellationToken.None);
        await repository.UpsertAsync(clientId, "gpt-4o", date, 30, 15, CancellationToken.None);

        var sample = await db.ClientTokenUsageSamples.SingleAsync();
        Assert.Equal(130, sample.InputTokens);
        Assert.Equal(40, sample.OutputTokens);
    }

    [Fact]
    public async Task RefreshAsync_WithSingleEvent_RebuildsDailyRollup()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        db.ProCursorTokenUsageEvents.Add(
            new ProCursorTokenUsageEvent(
                Guid.NewGuid(),
                clientId,
                sourceId,
                "Platform Wiki",
                $"pcidx:test:{Guid.NewGuid():N}",
                new DateTimeOffset(2026, 4, 4, 8, 0, 0, TimeSpan.Zero),
                ProCursorTokenUsageCallType.Embedding,
                "text-embedding-3-small",
                "text-embedding-3-small",
                "cl100k_base",
                120,
                0,
                false,
                0.00012m,
                true));
        await db.SaveChangesAsync();

        var service = new ProCursorTokenUsageAggregationService(db);

        var rebuilt = await service.RefreshAsync(new DateOnly(2026, 4, 4), new DateOnly(2026, 4, 4), clientId, false);

        Assert.Equal(2, rebuilt);
        var rollups = await db.ProCursorTokenUsageRollups
            .Where(item => item.Granularity == ProCursorTokenUsageGranularity.Daily)
            .ToListAsync();
        Assert.NotEmpty(rollups);
        Assert.Contains(rollups, rollup => rollup.TotalTokens == 120 && rollup.EventCount == 1);
    }

    private static MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"UsageReportingModuleTests_{Guid.NewGuid()}")
            .Options;

        return new MeisterProPRDbContext(options);
    }
}

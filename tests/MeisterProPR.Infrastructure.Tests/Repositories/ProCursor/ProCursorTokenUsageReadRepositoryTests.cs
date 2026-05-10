// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Tests.Repositories.ProCursor;

public sealed class ProCursorTokenUsageReadRepositoryTests
{
    private static (MeisterProPRDbContext Control, ProCursorOperationalDbContext Operational) CreateContexts()
    {
        var controlOptions = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"control-{Guid.NewGuid():N}")
            .Options;
        var operationalOptions = new DbContextOptionsBuilder<ProCursorOperationalDbContext>()
            .UseInMemoryDatabase($"operational-{Guid.NewGuid():N}")
            .Options;
        return (new MeisterProPRDbContext(controlOptions), new ProCursorOperationalDbContext(operationalOptions));
    }

    private static ProCursorTokenUsageReadRepository CreateRepository(
        ProCursorOperationalDbContext operationalDb,
        MeisterProPRDbContext controlDb)
    {
        return new ProCursorTokenUsageReadRepository(operationalDb, new ProCursorKnowledgeSourceRepository(controlDb));
    }

    [Fact]
    public async Task GetClientUsageAsync_AggregatesPersistedEvents()
    {
        var contexts = CreateContexts();
        await using var controlDb = contexts.Control;
        await using var operationalDb = contexts.Operational;
        var clientId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        controlDb.ProCursorKnowledgeSources.Add(CreateSource(sourceId, clientId, "Platform Wiki", "wiki-a", ProCursorSourceKind.AdoWiki));
        operationalDb.ProCursorTokenUsageEvents.Add(
            CreateEvent(
                clientId,
                sourceId,
                "Platform Wiki",
                "pcidx:test:embedding:0",
                new DateTimeOffset(2026, 4, 4, 8, 0, 0, TimeSpan.Zero),
                120,
                0,
                0.00012m));
        operationalDb.ProCursorTokenUsageEvents.Add(
            CreateEvent(
                clientId,
                sourceId,
                "Platform Wiki",
                "pcidx:test:embedding:1",
                new DateTimeOffset(2026, 4, 4, 9, 0, 0, TimeSpan.Zero),
                80,
                0,
                0.00008m));
        await controlDb.SaveChangesAsync();
        await operationalDb.SaveChangesAsync();

        var repository = CreateRepository(operationalDb, controlDb);

        var result = await repository.GetClientUsageAsync(
            clientId,
            new DateOnly(2026, 4, 4),
            new DateOnly(2026, 4, 4),
            ProCursorTokenUsageGranularity.Daily,
            "source");

        Assert.Equal(200, result.Totals.TotalTokens);
        Assert.Equal(2, result.Totals.EventCount);
        Assert.Single(result.TopSources);
        Assert.Single(result.Series);
        Assert.Single(result.Series[0].Breakdown);
    }

    [Fact]
    public async Task GetTopSourcesAsync_OrdersSourcesByDescendingTokenUsage()
    {
        var contexts = CreateContexts();
        await using var controlDb = contexts.Control;
        await using var operationalDb = contexts.Operational;
        var clientId = Guid.NewGuid();
        var dominantSourceId = Guid.NewGuid();
        var smallerSourceId = Guid.NewGuid();

        controlDb.ProCursorKnowledgeSources.AddRange(
            CreateSource(dominantSourceId, clientId, "Dominant Source", "repo-dominant"),
            CreateSource(smallerSourceId, clientId, "Smaller Source", "repo-smaller"));
        operationalDb.ProCursorTokenUsageEvents.AddRange(
            CreateEvent(
                clientId,
                dominantSourceId,
                "Dominant Source",
                "top-source:0",
                new DateTimeOffset(2026, 4, 4, 8, 0, 0, TimeSpan.Zero),
                300,
                0,
                0.0003m),
            CreateEvent(
                clientId,
                smallerSourceId,
                "Smaller Source",
                "top-source:1",
                new DateTimeOffset(2026, 4, 4, 9, 0, 0, TimeSpan.Zero),
                100,
                0,
                0.0001m));
        await controlDb.SaveChangesAsync();
        await operationalDb.SaveChangesAsync();

        var repository = CreateRepository(operationalDb, controlDb);

        var result = await repository.GetTopSourcesAsync(
            clientId,
            new DateOnly(2026, 4, 4),
            new DateOnly(2026, 4, 4),
            10);

        Assert.Equal(2, result.Count);
        Assert.Equal("Dominant Source", result[0].SourceDisplayName);
        Assert.Equal(300, result[0].TotalTokens);
        Assert.Equal("Smaller Source", result[1].SourceDisplayName);
    }

    [Fact]
    public async Task ExportAsync_ReturnsOneRowPerEventWithinRange()
    {
        var contexts = CreateContexts();
        await using var controlDb = contexts.Control;
        await using var operationalDb = contexts.Operational;
        var clientId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();

        controlDb.ProCursorKnowledgeSources.Add(CreateSource(sourceId, clientId, "Platform Wiki", "wiki-a", ProCursorSourceKind.AdoWiki));
        operationalDb.ProCursorTokenUsageEvents.AddRange(
            CreateEvent(
                clientId,
                sourceId,
                "Platform Wiki",
                "export:0",
                new DateTimeOffset(2026, 4, 4, 8, 0, 0, TimeSpan.Zero),
                120,
                0,
                0.00012m,
                "/docs/intro.md"),
            CreateEvent(
                clientId,
                sourceId,
                "Platform Wiki",
                "export:1",
                new DateTimeOffset(2026, 4, 5, 8, 0, 0, TimeSpan.Zero),
                60,
                0,
                0.00006m,
                "/docs/setup.md"));
        await controlDb.SaveChangesAsync();
        await operationalDb.SaveChangesAsync();

        var repository = CreateRepository(operationalDb, controlDb);

        var result = await repository.ExportAsync(
            clientId,
            new DateOnly(2026, 4, 4),
            new DateOnly(2026, 4, 5),
            null);

        Assert.Equal(2, result.Count);
        Assert.Equal(new DateOnly(2026, 4, 4), result[0].Date);
        Assert.Equal("/docs/intro.md", result[0].SourcePath);
        Assert.Equal(new DateOnly(2026, 4, 5), result[1].Date);
        Assert.Equal("/docs/setup.md", result[1].SourcePath);
    }

    [Fact]
    public async Task GetSourceUsageAsync_ReturnsSourceTotalsAndModelBreakdown()
    {
        var contexts = CreateContexts();
        await using var controlDb = contexts.Control;
        await using var operationalDb = contexts.Operational;
        var clientId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var otherSourceId = Guid.NewGuid();

        controlDb.ProCursorKnowledgeSources.AddRange(
            CreateSource(sourceId, clientId, "Platform Wiki", "wiki-a", ProCursorSourceKind.AdoWiki),
            CreateSource(otherSourceId, clientId, "Ignored Source", "wiki-b", ProCursorSourceKind.AdoWiki));
        operationalDb.ProCursorTokenUsageEvents.AddRange(
            CreateEvent(
                clientId,
                sourceId,
                "Platform Wiki",
                "source-usage:0",
                new DateTimeOffset(2026, 4, 4, 8, 0, 0, TimeSpan.Zero),
                120,
                0,
                0.00012m,
                modelName: "text-embedding-3-small"),
            CreateEvent(
                clientId,
                sourceId,
                "Platform Wiki",
                "source-usage:1",
                new DateTimeOffset(2026, 4, 4, 9, 0, 0, TimeSpan.Zero),
                80,
                40,
                0.0002m,
                modelName: "gpt-4.1-mini"),
            CreateEvent(
                clientId,
                otherSourceId,
                "Ignored Source",
                "source-usage:2",
                new DateTimeOffset(2026, 4, 4, 10, 0, 0, TimeSpan.Zero),
                999,
                0,
                0.00099m));
        await controlDb.SaveChangesAsync();
        await operationalDb.SaveChangesAsync();

        var repository = CreateRepository(operationalDb, controlDb);

        var result = await repository.GetSourceUsageAsync(
            clientId,
            sourceId,
            new DateOnly(2026, 4, 4),
            new DateOnly(2026, 4, 4),
            ProCursorTokenUsageGranularity.Daily);

        Assert.NotNull(result);
        Assert.Equal("Platform Wiki", result!.SourceDisplayName);
        Assert.Equal(240, result.Totals.TotalTokens);
        Assert.Equal(2, result.Totals.EventCount);
        Assert.Equal(2, result.ByModel.Count);
        Assert.Equal("gpt-4.1-mini", result.ByModel[0].ModelName);
        Assert.Equal("text-embedding-3-small", result.ByModel[1].ModelName);
        Assert.Single(result.Series);
        Assert.Equal(2, result.Series[0].Breakdown.Count);
        Assert.Contains(
            $"/admin/clients/{clientId}/procursor/sources/{sourceId}/token-usage/events",
            result.RecentEventsHref,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRecentEventsAsync_ReturnsRecentSafeEventsNewestFirst()
    {
        var contexts = CreateContexts();
        await using var controlDb = contexts.Control;
        await using var operationalDb = contexts.Operational;
        var clientId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var chunkId = Guid.NewGuid();

        controlDb.ProCursorKnowledgeSources.Add(CreateSource(sourceId, clientId, "Platform Wiki", "wiki-a", ProCursorSourceKind.AdoWiki));
        operationalDb.ProCursorTokenUsageEvents.AddRange(
            CreateEvent(
                clientId,
                sourceId,
                "Platform Wiki",
                "recent-events:0",
                new DateTimeOffset(2026, 4, 4, 8, 0, 0, TimeSpan.Zero),
                120,
                0,
                0.00012m,
                "/docs/intro.md",
                resourceId: "ado://wiki/intro",
                knowledgeChunkId: chunkId),
            CreateEvent(
                clientId,
                sourceId,
                "Platform Wiki",
                "recent-events:1",
                new DateTimeOffset(2026, 4, 4, 9, 0, 0, TimeSpan.Zero),
                60,
                10,
                0.00007m,
                "/docs/setup.md",
                resourceId: "ado://wiki/setup"));
        await controlDb.SaveChangesAsync();
        await operationalDb.SaveChangesAsync();

        var repository = CreateRepository(operationalDb, controlDb);

        var result = await repository.GetRecentEventsAsync(clientId, sourceId, 10);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Items.Count);
        Assert.Equal("recent-events:1", result.Items[0].RequestId);
        Assert.Equal("/docs/setup.md", result.Items[0].SourcePath);
        Assert.Equal("ado://wiki/setup", result.Items[0].ResourceId);
        Assert.Equal("recent-events:0", result.Items[1].RequestId);
        Assert.Equal(chunkId, result.Items[1].KnowledgeChunkId);
    }

    [Fact]
    public async Task GetRecentEventsAsync_DoesNotExposeSafeMetadataJson()
    {
        var contexts = CreateContexts();
        await using var controlDb = contexts.Control;
        await using var operationalDb = contexts.Operational;
        var clientId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        const string safeMetadataMarker = "private-safe-metadata-marker";

        controlDb.ProCursorKnowledgeSources.Add(CreateSource(sourceId, clientId, "Platform Wiki", "wiki-a", ProCursorSourceKind.AdoWiki));
        operationalDb.ProCursorTokenUsageEvents.Add(
            CreateEvent(
                clientId,
                sourceId,
                "Platform Wiki",
                "recent-events:safe-metadata",
                new DateTimeOffset(2026, 4, 4, 8, 0, 0, TimeSpan.Zero),
                120,
                0,
                0.00012m,
                safeMetadataJson: $"{{\"traceId\":\"{safeMetadataMarker}\"}}"));
        await controlDb.SaveChangesAsync();
        await operationalDb.SaveChangesAsync();

        var repository = CreateRepository(operationalDb, controlDb);

        var result = await repository.GetRecentEventsAsync(clientId, sourceId, 10);

        Assert.NotNull(result);
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("safeMetadataJson", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(safeMetadataMarker, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRecentEventsAsync_ZeroLimit_ReturnsEmptyCollection()
    {
        var contexts = CreateContexts();
        await using var controlDb = contexts.Control;
        await using var operationalDb = contexts.Operational;
        var clientId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();

        controlDb.ProCursorKnowledgeSources.Add(CreateSource(sourceId, clientId, "Platform Wiki", "wiki-a", ProCursorSourceKind.AdoWiki));
        operationalDb.ProCursorTokenUsageEvents.Add(
            CreateEvent(
                clientId,
                sourceId,
                "Platform Wiki",
                "recent-events:zero-limit",
                new DateTimeOffset(2026, 4, 4, 8, 0, 0, TimeSpan.Zero),
                120,
                0,
                0.00012m));
        await controlDb.SaveChangesAsync();
        await operationalDb.SaveChangesAsync();

        var repository = CreateRepository(operationalDb, controlDb);

        var result = await repository.GetRecentEventsAsync(clientId, sourceId, 0);

        Assert.NotNull(result);
        Assert.Empty(result!.Items);
    }

    private static ProCursorKnowledgeSource CreateSource(
        Guid sourceId,
        Guid clientId,
        string displayName,
        string repositoryId,
        ProCursorSourceKind sourceKind = ProCursorSourceKind.Repository)
    {
        return new ProCursorKnowledgeSource(
            sourceId,
            clientId,
            displayName,
            sourceKind,
            "https://dev.azure.com/test-org",
            "project-a",
            repositoryId,
            sourceKind == ProCursorSourceKind.AdoWiki ? "wikiMain" : "main",
            null,
            true,
            "auto");
    }

    private static ProCursorTokenUsageEvent CreateEvent(
        Guid clientId,
        Guid sourceId,
        string sourceDisplayName,
        string requestId,
        DateTimeOffset occurredAtUtc,
        long promptTokens,
        long completionTokens,
        decimal estimatedCostUsd,
        string? sourcePath = null,
        string modelName = "text-embedding-3-small",
        string? resourceId = null,
        Guid? knowledgeChunkId = null,
        string? safeMetadataJson = null)
    {
        return new ProCursorTokenUsageEvent(
            Guid.NewGuid(),
            clientId,
            sourceId,
            sourceDisplayName,
            requestId,
            occurredAtUtc,
            ProCursorTokenUsageCallType.Embedding,
            modelName,
            modelName,
            "cl100k_base",
            promptTokens,
            completionTokens,
            true,
            estimatedCostUsd,
            true,
            resourceId: resourceId,
            sourcePath: sourcePath,
            knowledgeChunkId: knowledgeChunkId,
            safeMetadataJson: safeMetadataJson);
    }
}

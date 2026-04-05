// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterProPR.Infrastructure.Tests.Repositories.ProCursor;

public sealed class ProCursorTokenUsageRecorderTests
{
    private static DbContextOptions<MeisterProPRDbContext> CreateOptions()
    {
        return new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    [Fact]
    public async Task RecordAsync_DuplicateRequestId_IgnoresSecondEvent()
    {
        var options = CreateOptions();
        await using var db = new MeisterProPRDbContext(options);
        var recorder = new EfProCursorTokenUsageRecorder(new TestDbContextFactory(options), NullLogger<EfProCursorTokenUsageRecorder>.Instance);
        var request = CreateRequest(requestId: "pcidx:test:embedding:0");

        await recorder.RecordAsync(request);
        await recorder.RecordAsync(request);

        Assert.Equal(1, await db.ProCursorTokenUsageEvents.CountAsync());
    }

    [Fact]
    public async Task RecordAsync_PersistsEstimatedFallbackFlagsAndSanitizedMetadata()
    {
        var options = CreateOptions();
        await using var db = new MeisterProPRDbContext(options);
        var recorder = new EfProCursorTokenUsageRecorder(new TestDbContextFactory(options), NullLogger<EfProCursorTokenUsageRecorder>.Instance);

        await recorder.RecordAsync(CreateRequest(
            requestId: "pcidx:test:embedding:1",
            safeMetadataJson: "{\u0000\"source\":\"wiki\"}",
            tokensEstimated: true,
            costEstimated: true,
            promptTokens: 144,
            estimatedCostUsd: 0.00014m));

        var recorded = await db.ProCursorTokenUsageEvents.SingleAsync();
        Assert.True(recorded.TokensEstimated);
        Assert.True(recorded.CostEstimated);
        Assert.Equal(144, recorded.TotalTokens);
        Assert.Equal("{\"source\":\"wiki\"}", recorded.SafeMetadataJson);
    }

    private static ProCursorTokenUsageCaptureRequest CreateRequest(
        string requestId,
        string? safeMetadataJson = null,
        bool tokensEstimated = true,
        bool costEstimated = true,
        long promptTokens = 120,
        decimal? estimatedCostUsd = 0.00012m)
    {
        return new ProCursorTokenUsageCaptureRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Platform Wiki",
            requestId,
            DateTimeOffset.UtcNow,
            MeisterProPR.Domain.Enums.ProCursorTokenUsageCallType.Embedding,
            "text-embedding-3-small",
            "text-embedding-3-small",
            "cl100k_base",
            promptTokens,
            0,
            promptTokens,
            tokensEstimated,
            estimatedCostUsd,
            costEstimated,
            SafeMetadataJson: safeMetadataJson);
    }

    private sealed class TestDbContextFactory(DbContextOptions<MeisterProPRDbContext> options) : IDbContextFactory<MeisterProPRDbContext>
    {
        public MeisterProPRDbContext CreateDbContext() => new(options);

        public Task<MeisterProPRDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new MeisterProPRDbContext(options));
    }
}

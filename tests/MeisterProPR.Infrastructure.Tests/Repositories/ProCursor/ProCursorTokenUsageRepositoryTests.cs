// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterProPR.Infrastructure.Tests.Repositories.ProCursor;

public sealed class ProCursorTokenUsageRepositoryTests
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
        var factory = new TestDbContextFactory(options);
        var recorder = new EfProCursorTokenUsageRecorder(factory, NullLogger<EfProCursorTokenUsageRecorder>.Instance);
        var request = new ProCursorTokenUsageCaptureRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Platform Wiki",
            "pcidx:test:embedding:0",
            DateTimeOffset.UtcNow,
            ProCursorTokenUsageCallType.Embedding,
            "text-embedding-3-small",
            "text-embedding-3-small",
            "cl100k_base",
            120,
            0,
            120,
            true,
            0.00012m,
            true);

        await recorder.RecordAsync(request);
        await recorder.RecordAsync(request);

        Assert.Equal(1, await db.ProCursorTokenUsageEvents.CountAsync());
    }

    private sealed class TestDbContextFactory(DbContextOptions<MeisterProPRDbContext> options)
        : IDbContextFactory<MeisterProPRDbContext>
    {
        public MeisterProPRDbContext CreateDbContext()
        {
            return new MeisterProPRDbContext(options);
        }

        public Task<MeisterProPRDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new MeisterProPRDbContext(options));
        }
    }
}

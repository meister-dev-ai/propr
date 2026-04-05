// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Repositories;
using MeisterProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using FactAttribute = Xunit.SkippableFactAttribute;
using TheoryAttribute = Xunit.SkippableTheoryAttribute;

namespace MeisterProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Integration tests for <see cref="EfReviewPrScanRepository" /> against a real PostgreSQL instance.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class EfReviewPrScanRepositoryTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private static readonly Guid SeedClientId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
    private MeisterProPRDbContext _dbContext = null!;
    private EfReviewPrScanRepository _repo = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);

        if (!await this._dbContext.Clients.AnyAsync(c => c.Id == SeedClientId))
        {
            this._dbContext.Clients.Add(
                new ClientRecord
                {
                    Id = SeedClientId,
                    DisplayName = "Review Scan Test Client",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            await this._dbContext.SaveChangesAsync();
        }

        await this._dbContext.ReviewPrScanThreads.ExecuteDeleteAsync();
        await this._dbContext.ReviewPrScans.ExecuteDeleteAsync();
        this._repo = new EfReviewPrScanRepository(this._dbContext);
    }

    public async Task DisposeAsync()
    {
        if (this._dbContext is not null)
        {
            await this._dbContext.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetAsync_WhenNotExists_ReturnsNull()
    {
        var result = await this._repo.GetAsync(Guid.NewGuid(), "repo", 1);
        Assert.Null(result);
    }

    [Fact]
    public async Task UpsertAsync_ThenGet_ReturnsSavedRecord()
    {
        var scan = new ReviewPrScan(Guid.NewGuid(), SeedClientId, "repo-1", 42, "iter-3");

        await this._repo.UpsertAsync(scan);

        var retrieved = await this._repo.GetAsync(SeedClientId, "repo-1", 42);
        Assert.NotNull(retrieved);
        Assert.Equal(SeedClientId, retrieved.ClientId);
        Assert.Equal("repo-1", retrieved.RepositoryId);
        Assert.Equal(42, retrieved.PullRequestId);
        Assert.Equal("iter-3", retrieved.LastProcessedCommitId);
    }

    [Fact]
    public async Task UpsertAsync_CalledTwice_UpdatesLastProcessedCommitId()
    {
        var scan = new ReviewPrScan(Guid.NewGuid(), SeedClientId, "repo-upd", 10, "iter-1");
        await this._repo.UpsertAsync(scan);

        var updated = new ReviewPrScan(scan.Id, SeedClientId, "repo-upd", 10, "iter-5");
        await this._repo.UpsertAsync(updated);

        var retrieved = await this._repo.GetAsync(SeedClientId, "repo-upd", 10);
        Assert.NotNull(retrieved);
        Assert.Equal("iter-5", retrieved.LastProcessedCommitId);
    }

    [Fact]
    public async Task UpsertAsync_WithThreads_PersistsThreadRecords()
    {
        var scan = new ReviewPrScan(Guid.NewGuid(), SeedClientId, "repo-threads", 7, "iter-2");
        scan.Threads.Add(new ReviewPrScanThread { ReviewPrScanId = scan.Id, ThreadId = 101, LastSeenReplyCount = 3 });
        scan.Threads.Add(new ReviewPrScanThread { ReviewPrScanId = scan.Id, ThreadId = 202, LastSeenReplyCount = 1 });

        await this._repo.UpsertAsync(scan);

        var retrieved = await this._repo.GetAsync(SeedClientId, "repo-threads", 7);
        Assert.NotNull(retrieved);
        Assert.Equal(2, retrieved.Threads.Count);
        Assert.Contains(retrieved.Threads, t => t.ThreadId == 101 && t.LastSeenReplyCount == 3);
        Assert.Contains(retrieved.Threads, t => t.ThreadId == 202 && t.LastSeenReplyCount == 1);
    }

    [Fact]
    public async Task UpsertAsync_UpdatedThreads_ReplacesExistingThreadRecords()
    {
        var scanId = Guid.NewGuid();
        var scan = new ReviewPrScan(scanId, SeedClientId, "repo-replace", 99, "iter-1");
        scan.Threads.Add(new ReviewPrScanThread { ReviewPrScanId = scanId, ThreadId = 10, LastSeenReplyCount = 2 });
        scan.Threads.Add(new ReviewPrScanThread { ReviewPrScanId = scanId, ThreadId = 20, LastSeenReplyCount = 1 });
        await this._repo.UpsertAsync(scan);

        // Update: thread 10 gets more replies, thread 20 is removed, thread 30 is new.
        var updated = new ReviewPrScan(scanId, SeedClientId, "repo-replace", 99, "iter-2");
        updated.Threads.Add(new ReviewPrScanThread { ReviewPrScanId = scanId, ThreadId = 10, LastSeenReplyCount = 5 });
        updated.Threads.Add(new ReviewPrScanThread { ReviewPrScanId = scanId, ThreadId = 30, LastSeenReplyCount = 0 });
        await this._repo.UpsertAsync(updated);

        var retrieved = await this._repo.GetAsync(SeedClientId, "repo-replace", 99);
        Assert.NotNull(retrieved);
        Assert.Equal(2, retrieved.Threads.Count);
        Assert.Contains(retrieved.Threads, t => t.ThreadId == 10 && t.LastSeenReplyCount == 5);
        Assert.Contains(retrieved.Threads, t => t.ThreadId == 30 && t.LastSeenReplyCount == 0);
        Assert.DoesNotContain(retrieved.Threads, t => t.ThreadId == 20);
    }

    [Fact]
    public async Task UpsertAsync_DifferentPrs_StoresSeparately()
    {
        var scan1 = new ReviewPrScan(Guid.NewGuid(), SeedClientId, "repo-sep", 1, "iter-1");
        var scan2 = new ReviewPrScan(Guid.NewGuid(), SeedClientId, "repo-sep", 2, "iter-1");

        await this._repo.UpsertAsync(scan1);
        await this._repo.UpsertAsync(scan2);

        Assert.NotNull(await this._repo.GetAsync(SeedClientId, "repo-sep", 1));
        Assert.NotNull(await this._repo.GetAsync(SeedClientId, "repo-sep", 2));
        Assert.Null(await this._repo.GetAsync(SeedClientId, "repo-sep", 3));
    }
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using FactAttribute = Xunit.SkippableFactAttribute;
using MeisterDev.ProPR.TestSupport;

namespace MeisterDev.ProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Integration tests for <see cref="EfReviewPrScanRepository" /> against a real PostgreSQL instance.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class EfReviewPrScanRepositoryTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private static readonly Guid SeedClientId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
    private readonly List<MeisterProPRDbContext> _contexts = [];
    private MeisterProPRDbContext _dbContext = null!;
    private EfReviewPrScanRepository _repo = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        this._dbContext = this.CreateDbContext();

        if (!await this._dbContext.Clients.AnyAsync(c => c.Id == SeedClientId))
        {
            this._dbContext.Clients.Add(
                new ClientRecord
                {
                    Id = SeedClientId,
                    TenantId = TenantCatalog.SystemTenantId,
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
        foreach (var context in this._contexts)
        {
            await context.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetAsync_WhenNotExists_ReturnsNull()
    {
        var result = await this._repo.GetAsync(Guid.NewGuid(), "repo", 1);
        Assert.Null(result);
    }

    [Fact]
    public async Task SetReviewWatermarkAsync_WhenNoScanExists_CreatesTheRecord()
    {
        await this._repo.SetReviewWatermarkAsync(SeedClientId, "repo-1", 42, "iter-3");

        var retrieved = await this._repo.GetAsync(SeedClientId, "repo-1", 42);
        Assert.NotNull(retrieved);
        Assert.Equal(SeedClientId, retrieved.ClientId);
        Assert.Equal("repo-1", retrieved.RepositoryId);
        Assert.Equal(42, retrieved.PullRequestId);
        Assert.Equal("iter-3", retrieved.LastProcessedCommitId);
        Assert.Empty(retrieved.Threads);
    }

    [Fact]
    public async Task SetReviewWatermarkAsync_WhenThreadsExist_LeavesEveryThreadRowUntouched()
    {
        await this._repo.SetReviewWatermarkAsync(SeedClientId, "repo-upd", 10, "iter-1");
        await this._repo.SetLastSeenReplyCountsAsync(
            SeedClientId,
            "repo-upd",
            10,
            new Dictionary<string, int> { ["101"] = 3, ["202"] = 1 });
        await this._repo.SetLastSeenStatusesAsync(
            SeedClientId,
            "repo-upd",
            10,
            new Dictionary<string, string?> { ["101"] = "Active", ["202"] = "Fixed" });

        await this._repo.SetReviewWatermarkAsync(SeedClientId, "repo-upd", 10, "iter-5");

        var retrieved = await this._repo.GetAsync(SeedClientId, "repo-upd", 10);
        Assert.NotNull(retrieved);
        Assert.Equal("iter-5", retrieved.LastProcessedCommitId);
        Assert.Equal(2, retrieved.Threads.Count);
        Assert.Contains(
            retrieved.Threads,
            t => t.ThreadId == "101" && t.LastSeenReplyCount == 3 && t.LastSeenStatus == "Active");
        Assert.Contains(
            retrieved.Threads,
            t => t.ThreadId == "202" && t.LastSeenReplyCount == 1 && t.LastSeenStatus == "Fixed");
    }

    [Fact]
    public async Task SetLastSeenReplyCountsAsync_UpdatesOnlyTheNamedThread()
    {
        await this._repo.SetReviewWatermarkAsync(SeedClientId, "repo-threads", 7, "iter-2");
        await this._repo.SetLastSeenReplyCountsAsync(
            SeedClientId,
            "repo-threads",
            7,
            new Dictionary<string, int> { ["101"] = 3, ["202"] = 1 });
        await this._repo.SetLastSeenStatusesAsync(
            SeedClientId,
            "repo-threads",
            7,
            new Dictionary<string, string?> { ["101"] = "Active", ["202"] = "Active" });

        await this._repo.SetLastSeenReplyCountsAsync(
            SeedClientId,
            "repo-threads",
            7,
            new Dictionary<string, int> { ["101"] = 9 });

        var retrieved = await this._repo.GetAsync(SeedClientId, "repo-threads", 7);
        Assert.NotNull(retrieved);
        Assert.Equal("iter-2", retrieved.LastProcessedCommitId);
        Assert.Contains(
            retrieved.Threads,
            t => t.ThreadId == "101" && t.LastSeenReplyCount == 9 && t.LastSeenStatus == "Active");
        Assert.Contains(
            retrieved.Threads,
            t => t.ThreadId == "202" && t.LastSeenReplyCount == 1 && t.LastSeenStatus == "Active");
    }

    [Fact]
    public async Task SetLastSeenStatusesAsync_UpdatesOnlyTheNamedThread()
    {
        await this._repo.SetReviewWatermarkAsync(SeedClientId, "repo-status", 8, "iter-2");
        await this._repo.SetLastSeenReplyCountsAsync(
            SeedClientId,
            "repo-status",
            8,
            new Dictionary<string, int> { ["101"] = 3, ["202"] = 1 });
        await this._repo.SetLastSeenStatusesAsync(
            SeedClientId,
            "repo-status",
            8,
            new Dictionary<string, string?> { ["101"] = "Active", ["202"] = "Active" });

        await this._repo.SetLastSeenStatusesAsync(
            SeedClientId,
            "repo-status",
            8,
            new Dictionary<string, string?> { ["101"] = null });

        var retrieved = await this._repo.GetAsync(SeedClientId, "repo-status", 8);
        Assert.NotNull(retrieved);
        Assert.Equal("iter-2", retrieved.LastProcessedCommitId);
        Assert.Contains(
            retrieved.Threads,
            t => t.ThreadId == "101" && t.LastSeenReplyCount == 3 && t.LastSeenStatus == null);
        Assert.Contains(
            retrieved.Threads,
            t => t.ThreadId == "202" && t.LastSeenReplyCount == 1 && t.LastSeenStatus == "Active");
    }

    [Fact]
    public async Task ThreadWrites_WhenNoScanExists_StoreNothing()
    {
        await this._repo.SetLastSeenStatusesAsync(
            SeedClientId,
            "repo-absent",
            3,
            new Dictionary<string, string?> { ["17"] = "Fixed" });
        await this._repo.SetLastSeenReplyCountsAsync(
            SeedClientId,
            "repo-absent",
            3,
            new Dictionary<string, int> { ["17"] = 2 });

        Assert.Null(await this._repo.GetAsync(SeedClientId, "repo-absent", 3));
    }

    [Fact]
    public async Task RetainOnlyThreadsAsync_RemovesTheThreadsNoLongerNamed()
    {
        await this._repo.SetReviewWatermarkAsync(SeedClientId, "repo-replace", 99, "iter-1");
        await this._repo.SetLastSeenReplyCountsAsync(
            SeedClientId,
            "repo-replace",
            99,
            new Dictionary<string, int> { ["10"] = 2, ["20"] = 1 });

        // Thread 10 is still reported, thread 20 has vanished, thread 30 is new.
        await this._repo.SetLastSeenReplyCountsAsync(
            SeedClientId,
            "repo-replace",
            99,
            new Dictionary<string, int> { ["10"] = 5, ["30"] = 0 });
        await this._repo.RetainOnlyThreadsAsync(SeedClientId, "repo-replace", 99, ["10", "30"]);

        var retrieved = await this._repo.GetAsync(SeedClientId, "repo-replace", 99);
        Assert.NotNull(retrieved);
        Assert.Equal(2, retrieved.Threads.Count);
        Assert.Contains(retrieved.Threads, t => t.ThreadId == "10" && t.LastSeenReplyCount == 5);
        Assert.Contains(retrieved.Threads, t => t.ThreadId == "30" && t.LastSeenReplyCount == 0);
        Assert.DoesNotContain(retrieved.Threads, t => t.ThreadId == "20");
    }

    [Fact]
    public async Task InterleavedWatermarkAndThreadStatusWrites_BothSurvive()
    {
        await this._repo.SetReviewWatermarkAsync(SeedClientId, "repo-interleave", 5, "iter-1");
        await this._repo.SetLastSeenReplyCountsAsync(
            SeedClientId,
            "repo-interleave",
            5,
            new Dictionary<string, int> { ["10"] = 4 });
        await this._repo.SetLastSeenStatusesAsync(
            SeedClientId,
            "repo-interleave",
            5,
            new Dictionary<string, string?> { ["10"] = "Active" });

        var statusWriter = new EfReviewPrScanRepository(this.CreateDbContext());
        var watermarkWriter = new EfReviewPrScanRepository(this.CreateDbContext());

        // The status writer reads first, the watermark writer advances in between, and only then does the
        // status writer apply the one fact it owns.
        Assert.NotNull(await statusWriter.GetAsync(SeedClientId, "repo-interleave", 5));

        await watermarkWriter.SetReviewWatermarkAsync(SeedClientId, "repo-interleave", 5, "iter-2");
        await statusWriter.SetLastSeenStatusesAsync(
            SeedClientId,
            "repo-interleave",
            5,
            new Dictionary<string, string?> { ["10"] = "Fixed" });

        var retrieved = await this._repo.GetAsync(SeedClientId, "repo-interleave", 5);
        Assert.NotNull(retrieved);
        Assert.Equal("iter-2", retrieved.LastProcessedCommitId);
        Assert.Contains(
            retrieved.Threads,
            t => t.ThreadId == "10" && t.LastSeenStatus == "Fixed" && t.LastSeenReplyCount == 4);
    }

    [Fact]
    public async Task RacingWatermarkAdvances_LastWriteWinsAndKeepsChildRows()
    {
        await this._repo.SetReviewWatermarkAsync(SeedClientId, "repo-race", 6, "iter-1");
        await this._repo.SetLastSeenReplyCountsAsync(
            SeedClientId,
            "repo-race",
            6,
            new Dictionary<string, int> { ["10"] = 4 });

        var firstWriter = new EfReviewPrScanRepository(this.CreateDbContext());
        var secondWriter = new EfReviewPrScanRepository(this.CreateDbContext());

        // Both writers observe the same starting state before either applies its advance.
        Assert.NotNull(await firstWriter.GetAsync(SeedClientId, "repo-race", 6));
        Assert.NotNull(await secondWriter.GetAsync(SeedClientId, "repo-race", 6));

        await firstWriter.SetReviewWatermarkAsync(SeedClientId, "repo-race", 6, "iter-2");
        await secondWriter.SetReviewWatermarkAsync(SeedClientId, "repo-race", 6, "iter-3");

        var retrieved = await this._repo.GetAsync(SeedClientId, "repo-race", 6);
        Assert.NotNull(retrieved);
        Assert.Equal("iter-3", retrieved.LastProcessedCommitId);
        Assert.Contains(retrieved.Threads, t => t.ThreadId == "10" && t.LastSeenReplyCount == 4);
    }

    [Fact]
    public async Task SetThreadPassWatermarkAsync_LeavesTheReviewWatermarkAndTheThreadCountersAlone()
    {
        await this._repo.SetReviewWatermarkAsync(SeedClientId, "repo-thread-wm", 8, "iter-1");
        await this._repo.SetLastSeenReplyCountsAsync(
            SeedClientId,
            "repo-thread-wm",
            8,
            new Dictionary<string, int> { ["10"] = 4 });

        await this._repo.SetThreadPassWatermarkAsync(SeedClientId, "repo-thread-wm", 8, "iter-2");

        var retrieved = await this._repo.GetAsync(SeedClientId, "repo-thread-wm", 8);
        Assert.NotNull(retrieved);
        Assert.Equal("iter-1", retrieved.LastProcessedCommitId);
        Assert.Equal("iter-2", retrieved.LastThreadPassRevisionKey);
        Assert.Contains(retrieved.Threads, t => t.ThreadId == "10" && t.LastSeenReplyCount == 4);
    }

    [Fact]
    public async Task SetThreadPassWatermarkAsync_WhenNoScanExists_CreatesTheRecordWithoutInventingAReviewWatermark()
    {
        await this._repo.SetThreadPassWatermarkAsync(SeedClientId, "repo-thread-first", 9, "iter-3");

        var retrieved = await this._repo.GetAsync(SeedClientId, "repo-thread-first", 9);
        Assert.NotNull(retrieved);
        Assert.Equal("iter-3", retrieved.LastThreadPassRevisionKey);
        Assert.Equal(string.Empty, retrieved.LastProcessedCommitId);
    }

    [Fact]
    public async Task SetReviewWatermarkAsync_LeavesTheThreadWatermarkAlone()
    {
        await this._repo.SetThreadPassWatermarkAsync(SeedClientId, "repo-both-wm", 11, "iter-3");

        await this._repo.SetReviewWatermarkAsync(SeedClientId, "repo-both-wm", 11, "iter-4");

        var retrieved = await this._repo.GetAsync(SeedClientId, "repo-both-wm", 11);
        Assert.NotNull(retrieved);
        Assert.Equal("iter-4", retrieved.LastProcessedCommitId);
        Assert.Equal("iter-3", retrieved.LastThreadPassRevisionKey);
    }

    [Fact]
    public async Task SetReviewWatermarkAsync_DifferentPrs_StoresSeparately()
    {
        await this._repo.SetReviewWatermarkAsync(SeedClientId, "repo-sep", 1, "iter-1");
        await this._repo.SetReviewWatermarkAsync(SeedClientId, "repo-sep", 2, "iter-1");

        Assert.NotNull(await this._repo.GetAsync(SeedClientId, "repo-sep", 1));
        Assert.NotNull(await this._repo.GetAsync(SeedClientId, "repo-sep", 2));
        Assert.Null(await this._repo.GetAsync(SeedClientId, "repo-sep", 3));
    }

    [Fact]
    public async Task SetPendingReviewRevisionAsync_NoRecordYet_CreatesOneWithNeitherWatermarkSet()
    {
        await this._repo.SetPendingReviewRevisionAsync(SeedClientId, "repo-pending-new", 21, "iter-5");

        var retrieved = await this._repo.GetAsync(SeedClientId, "repo-pending-new", 21);
        Assert.NotNull(retrieved);
        Assert.Equal("iter-5", retrieved.PendingReviewRevisionKey);
        Assert.NotNull(retrieved.PendingReviewDetectedAt);

        // Declining to review a revision is not a record of having reviewed one, and the threads were not
        // checked by the guard either.
        Assert.Equal(string.Empty, retrieved.LastProcessedCommitId);
        Assert.Equal(string.Empty, retrieved.LastThreadPassRevisionKey);
    }

    /// <summary>
    ///     The clock measures how long the pull request has been waiting, not how recently the crawler looked at
    ///     it. Restamping on every tick would make a pull request left for a week report as newly arrived.
    /// </summary>
    [Fact]
    public async Task SetPendingReviewRevisionAsync_SameRevisionAgain_LeavesTheDetectionTimeAlone()
    {
        await this._repo.SetPendingReviewRevisionAsync(SeedClientId, "repo-pending-same", 22, "iter-5");
        var firstDetectedAt = (await this._repo.GetAsync(SeedClientId, "repo-pending-same", 22))!
            .PendingReviewDetectedAt;

        await this._repo.SetPendingReviewRevisionAsync(SeedClientId, "repo-pending-same", 22, "iter-5");

        var retrieved = await this._repo.GetAsync(SeedClientId, "repo-pending-same", 22);
        Assert.NotNull(retrieved);
        Assert.Equal(firstDetectedAt, retrieved.PendingReviewDetectedAt);
    }

    [Fact]
    public async Task SetPendingReviewRevisionAsync_NewRevision_MovesTheDetectionTime()
    {
        await this._repo.SetPendingReviewRevisionAsync(SeedClientId, "repo-pending-moved", 23, "iter-5");
        var firstDetectedAt = (await this._repo.GetAsync(SeedClientId, "repo-pending-moved", 23))!
            .PendingReviewDetectedAt;

        await this._repo.SetPendingReviewRevisionAsync(SeedClientId, "repo-pending-moved", 23, "iter-6");

        var retrieved = await this._repo.GetAsync(SeedClientId, "repo-pending-moved", 23);
        Assert.NotNull(retrieved);
        Assert.Equal("iter-6", retrieved.PendingReviewRevisionKey);
        Assert.NotEqual(firstDetectedAt, retrieved.PendingReviewDetectedAt);
    }

    /// <summary>
    ///     Recording a declined revision must not disturb what the two passes recorded, in either direction.
    /// </summary>
    [Fact]
    public async Task SetPendingReviewRevisionAsync_AndBothWatermarks_KeepTheirOwnValues()
    {
        await this._repo.SetReviewWatermarkAsync(SeedClientId, "repo-pending-wm", 24, "iter-1");
        await this._repo.SetThreadPassWatermarkAsync(SeedClientId, "repo-pending-wm", 24, "iter-2");
        await this._repo.SetPendingReviewRevisionAsync(SeedClientId, "repo-pending-wm", 24, "iter-3");

        var retrieved = await this._repo.GetAsync(SeedClientId, "repo-pending-wm", 24);
        Assert.NotNull(retrieved);
        Assert.Equal("iter-1", retrieved.LastProcessedCommitId);
        Assert.Equal("iter-2", retrieved.LastThreadPassRevisionKey);
        Assert.Equal("iter-3", retrieved.PendingReviewRevisionKey);

        // And a later review of the pending revision retires it by writing its own watermark over the same
        // value, which is the whole mechanism by which the state clears.
        await this._repo.SetReviewWatermarkAsync(SeedClientId, "repo-pending-wm", 24, "iter-3");

        var reviewed = await this._repo.GetAsync(SeedClientId, "repo-pending-wm", 24);
        Assert.NotNull(reviewed);
        Assert.Equal(reviewed.PendingReviewRevisionKey, reviewed.LastProcessedCommitId);
    }

    private MeisterProPRDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        var context = new MeisterProPRDbContext(options);
        this._contexts.Add(context);
        return context;
    }
}

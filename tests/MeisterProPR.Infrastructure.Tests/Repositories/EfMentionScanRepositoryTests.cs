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
///     Integration tests for <see cref="EfMentionScanRepository" /> against a real PostgreSQL instance.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class EfMentionScanRepositoryTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    // Deterministic GUIDs so FK constraints are satisfied across test runs.
    private static readonly Guid SeedClientId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid ConfigId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private MeisterProPRDbContext _dbContext = null!;
    private EfMentionScanRepository _repo = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);

        // Seed client + crawl config for FK constraints.
        if (!await this._dbContext.Clients.AnyAsync(c => c.Id == SeedClientId))
        {
            this._dbContext.Clients.Add(
                new ClientRecord
                {
                    Id = SeedClientId,
                    DisplayName = "Test Scan Client",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            await this._dbContext.SaveChangesAsync();
        }

        if (!await this._dbContext.CrawlConfigurations.AnyAsync(c => c.Id == ConfigId))
        {
            this._dbContext.CrawlConfigurations.Add(
                new CrawlConfigurationRecord
                {
                    Id = ConfigId,
                    ClientId = SeedClientId,
                    OrganizationUrl = "https://dev.azure.com/test-org",
                    ProjectId = "test-proj",
                    CrawlIntervalSeconds = 60,
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            await this._dbContext.SaveChangesAsync();
        }

        await this._dbContext.MentionProjectScans.ExecuteDeleteAsync();
        await this._dbContext.MentionPrScans.ExecuteDeleteAsync();
        this._repo = new EfMentionScanRepository(this._dbContext);
    }

    public async Task DisposeAsync()
    {
        if (this._dbContext is not null)
        {
            await this._dbContext.DisposeAsync();
        }
    }


    [Fact]
    public async Task GetProjectScanAsync_WhenNotExists_ReturnsNull()
    {
        var result = await this._repo.GetProjectScanAsync(Guid.NewGuid());
        Assert.Null(result);
    }

    [Fact]
    public async Task UpsertProjectScanAsync_ThenGet_ReturnsSavedWatermark()
    {
        var lastScanned = DateTimeOffset.UtcNow.AddMinutes(-5);
        var scan = new MentionProjectScan(Guid.NewGuid(), ConfigId, lastScanned);

        await this._repo.UpsertProjectScanAsync(scan);

        var retrieved = await this._repo.GetProjectScanAsync(ConfigId);
        Assert.NotNull(retrieved);
        Assert.Equal(ConfigId, retrieved.CrawlConfigurationId);
        Assert.Equal(lastScanned.ToUnixTimeSeconds(), retrieved.LastScannedAt.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task UpsertProjectScanAsync_CalledTwice_UpdatesWatermark()
    {
        var scan = new MentionProjectScan(Guid.NewGuid(), ConfigId, DateTimeOffset.UtcNow.AddHours(-1));
        await this._repo.UpsertProjectScanAsync(scan);

        // Upsert with newer timestamp
        var newer = new MentionProjectScan(scan.Id, ConfigId, DateTimeOffset.UtcNow);
        await this._repo.UpsertProjectScanAsync(newer);

        var retrieved = await this._repo.GetProjectScanAsync(ConfigId);
        Assert.NotNull(retrieved);
        // Watermark should be updated (newer than 1 hour ago)
        Assert.True(retrieved.LastScannedAt > DateTimeOffset.UtcNow.AddMinutes(-5));
    }


    [Fact]
    public async Task GetPrScanAsync_WhenNotExists_ReturnsNull()
    {
        var result = await this._repo.GetPrScanAsync(Guid.NewGuid(), "repo", 1);
        Assert.Null(result);
    }

    [Fact]
    public async Task UpsertPrScanAsync_ThenGet_ReturnsSavedWatermark()
    {
        var lastSeen = DateTimeOffset.UtcNow.AddMinutes(-10);
        var scan = new MentionPrScan(Guid.NewGuid(), ConfigId, "repo-1", 42, lastSeen);

        await this._repo.UpsertPrScanAsync(scan);

        var retrieved = await this._repo.GetPrScanAsync(ConfigId, "repo-1", 42);
        Assert.NotNull(retrieved);
        Assert.Equal(42, retrieved.PullRequestId);
        Assert.Equal(lastSeen.ToUnixTimeSeconds(), retrieved.LastCommentSeenAt.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task UpsertPrScanAsync_CalledTwice_UpdatesWatermark()
    {
        var scan = new MentionPrScan(Guid.NewGuid(), ConfigId, "repo-2", 7, DateTimeOffset.UtcNow.AddHours(-2));
        await this._repo.UpsertPrScanAsync(scan);

        var updated = new MentionPrScan(scan.Id, ConfigId, "repo-2", 7, DateTimeOffset.UtcNow);
        await this._repo.UpsertPrScanAsync(updated);

        var retrieved = await this._repo.GetPrScanAsync(ConfigId, "repo-2", 7);
        Assert.NotNull(retrieved);
        Assert.True(retrieved.LastCommentSeenAt > DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task UpsertPrScanAsync_DifferentPrs_StoresSeparately()
    {
        var scan1 = new MentionPrScan(Guid.NewGuid(), ConfigId, "repo", 1, DateTimeOffset.UtcNow);
        var scan2 = new MentionPrScan(Guid.NewGuid(), ConfigId, "repo", 2, DateTimeOffset.UtcNow);

        await this._repo.UpsertPrScanAsync(scan1);
        await this._repo.UpsertPrScanAsync(scan2);

        Assert.NotNull(await this._repo.GetPrScanAsync(ConfigId, "repo", 1));
        Assert.NotNull(await this._repo.GetPrScanAsync(ConfigId, "repo", 2));
        Assert.Null(await this._repo.GetPrScanAsync(ConfigId, "repo", 3));
    }
}

using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Repositories;
using MeisterProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Tests for <see cref="PostgresClientRegistry.GetReviewerIdAsync" />.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class ClientRegistryTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private MeisterProPRDbContext _dbContext = null!;
    private PostgresClientRegistry _registry = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);
        await this._dbContext.Clients.ExecuteDeleteAsync();
        this._registry = new PostgresClientRegistry(this._dbContext, NullLogger<PostgresClientRegistry>.Instance);
    }

    public async Task DisposeAsync()
    {
        await this._dbContext.DisposeAsync();
    }

    [Fact]
    public async Task GetReviewerIdAsync_ClientWithNullReviewerId_ReturnsNull()
    {
        var record = await this.SeedClientAsync();
        var result = await this._registry.GetReviewerIdAsync(record.Id);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetReviewerIdAsync_ClientWithReviewerId_ReturnsGuid()
    {
        var reviewerId = Guid.NewGuid();
        var record = await this.SeedClientAsync(reviewerId);
        var result = await this._registry.GetReviewerIdAsync(record.Id);
        Assert.Equal(reviewerId, result);
    }

    // T001 — GetReviewerIdAsync returns null for unknown client ID

    [Fact]
    public async Task GetReviewerIdAsync_UnknownClientId_ReturnsNull()
    {
        var result = await this._registry.GetReviewerIdAsync(Guid.NewGuid());
        Assert.Null(result);
    }

    private async Task<ClientRecord> SeedClientAsync(Guid? reviewerId = null, string key = "test-key-abc123")
    {
        var record = new ClientRecord
        {
            Id = Guid.NewGuid(),
            Key = key,
            DisplayName = "Test Client",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            ReviewerId = reviewerId,
        };
        this._dbContext.Clients.Add(record);
        await this._dbContext.SaveChangesAsync();
        return record;
    }
}
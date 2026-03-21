using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Repositories;
using MeisterProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Integration tests for <see cref="PostgresClientRegistry" /> against a real PostgreSQL instance.
///     Uses a shared <see cref="PostgresContainerFixture" /> (one container for the whole collection)
///     to avoid the Podman port-binding instability of starting a container per test method.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class PostgresClientRegistryTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private MeisterProPRDbContext _dbContext = null!;
    private PostgresClientRegistry _registry = null!;

    public async Task DisposeAsync()
    {
        await this._dbContext.DisposeAsync();
    }

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);
        // Wipe client rows between tests (CASCADE removes crawl_configurations too).
        await this._dbContext.Clients.ExecuteDeleteAsync();
        this._registry = new PostgresClientRegistry(this._dbContext, NullLogger<PostgresClientRegistry>.Instance);
    }

    // ── GetClientIdByKeyAsync (T046) ──────────────────────────────────────────

    [Fact]
    public async Task GetClientIdByKeyAsync_ReturnsGuid_ForValidActiveKey()
    {
        var record = await this.SeedClientAsync("active-key-get-id");
        var result = await this._registry.GetClientIdByKeyAsync("active-key-get-id");
        Assert.NotNull(result);
        Assert.Equal(record.Id, result.Value);
    }

    [Fact]
    public async Task GetClientIdByKeyAsync_ReturnsNull_ForEmpty()
    {
        var result = await this._registry.GetClientIdByKeyAsync("");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetClientIdByKeyAsync_ReturnsNull_ForInactiveKey()
    {
        await this.SeedClientAsync("inactive-get-id", false);
        var result = await this._registry.GetClientIdByKeyAsync("inactive-get-id");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetClientIdByKeyAsync_ReturnsNull_ForUnknownKey()
    {
        var result = await this._registry.GetClientIdByKeyAsync("not-in-database");
        Assert.Null(result);
    }

    [Fact]
    public void IsValidKey_ReturnsFalse_ForEmpty()
    {
        Assert.False(this._registry.IsValidKey(""));
    }

    [Fact]
    public async Task IsValidKey_ReturnsFalse_ForInactiveKey()
    {
        await this.SeedClientAsync("inactive-key-xyz", false);
        Assert.False(this._registry.IsValidKey("inactive-key-xyz"));
    }

    [Fact]
    public void IsValidKey_ReturnsFalse_ForUnknownKey()
    {
        Assert.False(this._registry.IsValidKey("totally-unknown-key"));
    }

    // ── IsValidKey ────────────────────────────────────────────────────────────

    [Fact]
    public async Task IsValidKey_ReturnsTrue_ForActiveKey()
    {
        await this.SeedClientAsync("valid-key-abc123");
        Assert.True(this._registry.IsValidKey("valid-key-abc123"));
    }

    private async Task<ClientRecord> SeedClientAsync(string key, bool isActive = true)
    {
        var record = new ClientRecord
        {
            Id = Guid.NewGuid(),
            Key = key,
            DisplayName = "Test Client",
            IsActive = isActive,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        this._dbContext.Clients.Add(record);
        await this._dbContext.SaveChangesAsync();
        return record;
    }
}

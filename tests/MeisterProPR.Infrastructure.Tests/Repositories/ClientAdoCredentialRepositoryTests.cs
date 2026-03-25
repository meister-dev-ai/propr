using MeisterProPR.Application.DTOs;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MeisterProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Unit tests for <see cref="ClientAdoCredentialRepository" /> using an in-memory EF Core database.
///     Integration tests against a real PostgreSQL instance are covered by the docker-compose test environment.
/// </summary>
public sealed class ClientAdoCredentialRepositoryTests
{
    private static MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"TestDb_AdoCreds_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new MeisterProPRDbContext(options);
    }

    private static async Task<Guid> SeedClientAsync(MeisterProPRDbContext db)
    {
        var id = Guid.NewGuid();
        db.Clients.Add(
            new ClientRecord
            {
                Id = id,
                Key = $"key-{id}",
                DisplayName = "Test",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task ClearAsync_NullsAllThreeColumns()
    {
        await using var db = CreateContext();
        var clientId = await SeedClientAsync(db);
        var sut = new ClientAdoCredentialRepository(db);

        await sut.UpsertAsync(clientId, new ClientAdoCredentials("t", "c", "s"), CancellationToken.None);
        await sut.ClearAsync(clientId, CancellationToken.None);

        var result = await sut.GetByClientIdAsync(clientId, CancellationToken.None);
        Assert.Null(result);

        var record = await db.Clients.FindAsync(clientId);
        Assert.NotNull(record);
        Assert.Null(record.AdoTenantId);
        Assert.Null(record.AdoClientId);
        Assert.Null(record.AdoClientSecret);
    }

    [Fact]
    public async Task GetByClientIdAsync_NoCredentials_ReturnsNull()
    {
        await using var db = CreateContext();
        var clientId = await SeedClientAsync(db);
        var sut = new ClientAdoCredentialRepository(db);

        var result = await sut.GetByClientIdAsync(clientId, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task UpsertAsync_CalledTwice_UpdatesExistingRow()
    {
        await using var db = CreateContext();
        var clientId = await SeedClientAsync(db);
        var sut = new ClientAdoCredentialRepository(db);

        await sut.UpsertAsync(clientId, new ClientAdoCredentials("t1", "c1", "s1"), CancellationToken.None);
        await sut.UpsertAsync(clientId, new ClientAdoCredentials("t2", "c2", "s2"), CancellationToken.None);

        var result = await sut.GetByClientIdAsync(clientId, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal("t2", result.TenantId);
        Assert.Equal("c2", result.ClientId);
        Assert.Equal("s2", result.Secret);

        // Ensure no duplicate rows in the clients table
        var count = await db.Clients.CountAsync(c => c.Id == clientId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task UpsertAsync_ThenGetByClientId_ReturnsStoredCredentials()
    {
        await using var db = CreateContext();
        var clientId = await SeedClientAsync(db);
        var sut = new ClientAdoCredentialRepository(db);
        var credentials = new ClientAdoCredentials("tenant-abc", "client-abc", "secret-abc");

        await sut.UpsertAsync(clientId, credentials, CancellationToken.None);
        var result = await sut.GetByClientIdAsync(clientId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("tenant-abc", result.TenantId);
        Assert.Equal("client-abc", result.ClientId);
        Assert.Equal("secret-abc", result.Secret);
    }
}

using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Tests for <see cref="FindingDismissalRepository" /> using EF Core in-memory database (US3, T018).
/// </summary>
public sealed class FindingDismissalRepositoryTests
{
    private static MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new MeisterProPRDbContext(options);
    }

    private static async Task<Guid> SeedClientAsync(MeisterProPRDbContext db)
    {
        var clientId = Guid.NewGuid();
        db.Clients.Add(new ClientRecord { Id = clientId, DisplayName = "Test Client", Key = Guid.NewGuid().ToString() });
        await db.SaveChangesAsync();
        return clientId;
    }

    // T018 — Add then GetByClient returns the item
    [Fact]
    public async Task AddAsync_ThenGetByClientAsync_ReturnsDismissal()
    {
        await using var db = CreateContext();
        var clientId = await SeedClientAsync(db);
        var repo = new FindingDismissalRepository(db);

        var dismissal = new FindingDismissal(Guid.NewGuid(), clientId, "use idisposable pattern", "Accepted", "Use IDisposable pattern here");
        await repo.AddAsync(dismissal);

        var results = await repo.GetByClientAsync(clientId);

        Assert.Single(results);
        Assert.Equal(dismissal.Id, results[0].Id);
        Assert.Equal("use idisposable pattern", results[0].PatternText);
        Assert.Equal("Accepted", results[0].Label);
    }

    // T018 — Delete removes the item
    [Fact]
    public async Task DeleteAsync_RemovesDismissal()
    {
        await using var db = CreateContext();
        var clientId = await SeedClientAsync(db);
        var repo = new FindingDismissalRepository(db);

        var dismissal = new FindingDismissal(Guid.NewGuid(), clientId, "pattern", null, "original");
        await repo.AddAsync(dismissal);

        var deleted = await repo.DeleteAsync(dismissal.Id);
        var results = await repo.GetByClientAsync(clientId);

        Assert.True(deleted);
        Assert.Empty(results);
    }

    // T018 — GetById after delete returns null
    [Fact]
    public async Task GetByIdAsync_AfterDelete_ReturnsNull()
    {
        await using var db = CreateContext();
        var clientId = await SeedClientAsync(db);
        var repo = new FindingDismissalRepository(db);

        var dismissal = new FindingDismissal(Guid.NewGuid(), clientId, "pattern", null, "original");
        await repo.AddAsync(dismissal);
        await repo.DeleteAsync(dismissal.Id);

        var result = await repo.GetByIdAsync(dismissal.Id);

        Assert.Null(result);
    }

    // T018 — Delete on unknown id returns false
    [Fact]
    public async Task DeleteAsync_UnknownId_ReturnsFalse()
    {
        await using var db = CreateContext();
        var repo = new FindingDismissalRepository(db);

        var result = await repo.DeleteAsync(Guid.NewGuid());

        Assert.False(result);
    }
}

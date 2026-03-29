using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Tests.Repositories;

/// <summary>Unit tests for <see cref="UserPatRepository"/> using EF Core in-memory database.</summary>
public sealed class UserPatRepositoryTests
{
    private static MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new MeisterProPRDbContext(options);
    }

    [Fact]
    public async Task ListForUserAsync_WithRevokedAndActivePats_ReturnsOnlyActivePats()
    {
        // Arrange
        await using var db = CreateContext();
        var userId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.UserPats.AddRange(
            new UserPatRecord
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TokenHash = "active-hash",
                Label = "Active PAT",
                IsRevoked = false,
                CreatedAt = now,
            },
            new UserPatRecord
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TokenHash = "revoked-hash",
                Label = "Revoked PAT",
                IsRevoked = true,
                CreatedAt = now.AddMinutes(-10),
            });
        await db.SaveChangesAsync();

        var repo = new UserPatRepository(db);

        // Act
        var results = await repo.ListForUserAsync(userId);

        // Assert: only the active PAT is returned
        Assert.Single(results);
        Assert.Equal("Active PAT", results[0].Label);
        Assert.False(results[0].IsRevoked);
    }

    [Fact]
    public async Task ListForUserAsync_AfterRevoke_DoesNotReturnRevokedPat()
    {
        // Arrange
        await using var db = CreateContext();
        var userId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var patId = Guid.NewGuid();

        db.UserPats.Add(new UserPatRecord
        {
            Id = patId,
            UserId = userId,
            TokenHash = "some-hash",
            Label = "My PAT",
            IsRevoked = false,
            CreatedAt = now,
        });
        await db.SaveChangesAsync();

        // Simulate revocation
        var record = await db.UserPats.FindAsync(patId);
        record!.IsRevoked = true;
        await db.SaveChangesAsync();

        var repo = new UserPatRepository(db);

        // Act — simulates what happens on page refresh
        var results = await repo.ListForUserAsync(userId);

        // Assert: revoked PAT is gone from the list
        Assert.Empty(results);
    }

    [Fact]
    public async Task ListForUserAsync_WithNoActivePats_ReturnsEmpty()
    {
        // Arrange
        await using var db = CreateContext();
        var userId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.UserPats.Add(new UserPatRecord
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = "hash",
            Label = "Old PAT",
            IsRevoked = true,
            CreatedAt = now,
        });
        await db.SaveChangesAsync();

        var repo = new UserPatRepository(db);

        // Act
        var results = await repo.ListForUserAsync(userId);

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public async Task ListForUserAsync_WithExpiredPat_DoesNotReturnExpiredPat()
    {
        // Arrange
        await using var db = CreateContext();
        var userId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.UserPats.AddRange(
            new UserPatRecord
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TokenHash = "active-hash",
                Label = "Active PAT",
                IsRevoked = false,
                ExpiresAt = null,
                CreatedAt = now,
            },
            new UserPatRecord
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TokenHash = "expired-hash",
                Label = "Expired PAT",
                IsRevoked = false,
                ExpiresAt = now.AddDays(-1),
                CreatedAt = now.AddDays(-2),
            });
        await db.SaveChangesAsync();

        var repo = new UserPatRepository(db);

        // Act
        var results = await repo.ListForUserAsync(userId);

        // Assert: only the non-expired PAT is returned
        Assert.Single(results);
        Assert.Equal("Active PAT", results[0].Label);
    }
}

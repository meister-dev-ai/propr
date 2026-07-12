// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Repositories;
using MeisterProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Verifies that <see cref="RefreshTokenRepository" /> enforces both session-policy bounds — the
///     absolute expiry and the idle timeout — and that a refresh advances the idle window.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class RefreshTokenRepositoryTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private static readonly SessionPolicy Policy = new()
    {
        IdleTimeout = TimeSpan.FromHours(8),
        AbsoluteLifetime = TimeSpan.FromHours(72),
    };

    private DbContextOptions<MeisterProPRDbContext> _options = null!;
    private Guid _userId;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        this._options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;

        this._userId = Guid.NewGuid();
        await using var db = this.NewContext();
        db.Set<AppUserRecord>()
            .Add(
                new AppUserRecord
                {
                    Id = this._userId,
                    Username = $"refresh-user-{this._userId:N}",
                    GlobalRole = AppUserRole.User,
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetActiveByHash_WithinIdleAndAbsoluteWindows_ReturnsToken()
    {
        var now = DateTimeOffset.UtcNow;
        var (hash, _) = await this.SeedTokenAsync(expiresAt: now.AddHours(72), lastUsedAt: now.AddHours(-1));

        await using var db = this.NewContext();
        var result = await new RefreshTokenRepository(db, Policy).GetActiveByHashAsync(hash);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task GetActiveByHash_WhenIdleLongerThanTimeout_ReturnsNull()
    {
        var now = DateTimeOffset.UtcNow;
        // Absolute expiry is still far off, but the last refresh was longer ago than the idle window.
        var (hash, _) = await this.SeedTokenAsync(expiresAt: now.AddHours(72), lastUsedAt: now.AddHours(-9));

        await using var db = this.NewContext();
        var result = await new RefreshTokenRepository(db, Policy).GetActiveByHashAsync(hash);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetActiveByHash_WhenPastAbsoluteExpiry_ReturnsNullEvenIfRecentlyUsed()
    {
        var now = DateTimeOffset.UtcNow;
        var (hash, _) = await this.SeedTokenAsync(expiresAt: now.AddMinutes(-1), lastUsedAt: now);

        await using var db = this.NewContext();
        var result = await new RefreshTokenRepository(db, Policy).GetActiveByHashAsync(hash);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetActiveByHash_WhenRevoked_ReturnsNull()
    {
        var now = DateTimeOffset.UtcNow;
        var (hash, _) = await this.SeedTokenAsync(
            expiresAt: now.AddHours(72),
            lastUsedAt: now,
            revokedAt: now.AddMinutes(-1));

        await using var db = this.NewContext();
        var result = await new RefreshTokenRepository(db, Policy).GetActiveByHashAsync(hash);

        Assert.Null(result);
    }

    [Fact]
    public async Task TouchLastUsedAsync_RevivesATokenThatHadFallenOutOfTheIdleWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var (hash, id) = await this.SeedTokenAsync(expiresAt: now.AddHours(72), lastUsedAt: now.AddHours(-9));

        await using (var db = this.NewContext())
        {
            Assert.Null(await new RefreshTokenRepository(db, Policy).GetActiveByHashAsync(hash));
        }

        await using (var db = this.NewContext())
        {
            await new RefreshTokenRepository(db, Policy).TouchLastUsedAsync(id, DateTimeOffset.UtcNow);
        }

        await using (var db = this.NewContext())
        {
            Assert.NotNull(await new RefreshTokenRepository(db, Policy).GetActiveByHashAsync(hash));
        }
    }

    private MeisterProPRDbContext NewContext()
    {
        return new MeisterProPRDbContext(this._options);
    }

    private async Task<(string Hash, Guid Id)> SeedTokenAsync(
        DateTimeOffset expiresAt,
        DateTimeOffset lastUsedAt,
        DateTimeOffset? revokedAt = null)
    {
        var id = Guid.NewGuid();
        var hash = $"hash-{Guid.NewGuid():N}";

        await using var db = this.NewContext();
        db.RefreshTokens.Add(
            new RefreshTokenRecord
            {
                Id = id,
                UserId = this._userId,
                TokenHash = hash,
                ExpiresAt = expiresAt,
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
                LastUsedAt = lastUsedAt,
                RevokedAt = revokedAt,
            });
        await db.SaveChangesAsync();

        return (hash, id);
    }
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IRefreshTokenRepository" />.</summary>
public sealed class RefreshTokenRepository(MeisterProPRDbContext db, SessionPolicy sessionPolicy)
    : IRefreshTokenRepository
{
    public async Task AddAsync(RefreshToken token, CancellationToken ct = default)
    {
        db.RefreshTokens.Add(
            new RefreshTokenRecord
            {
                Id = token.Id == Guid.Empty ? Guid.NewGuid() : token.Id,
                UserId = token.UserId,
                TokenHash = token.TokenHash,
                ExpiresAt = token.ExpiresAt,
                CreatedAt = token.CreatedAt,
                LastUsedAt = token.LastUsedAt,
                RevokedAt = token.RevokedAt,
            });
        await db.SaveChangesAsync(ct);
    }

    public async Task<RefreshToken?> GetActiveByHashAsync(string tokenHash, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var idleThreshold = now - sessionPolicy.IdleTimeout;
        var record = await db.RefreshTokens
            .FirstOrDefaultAsync(
                t =>
                    t.TokenHash == tokenHash &&
                    t.RevokedAt == null &&
                    t.ExpiresAt > now &&
                    t.LastUsedAt > idleThreshold,
                ct);

        return record is null
            ? null
            : new RefreshToken
            {
                Id = record.Id,
                UserId = record.UserId,
                TokenHash = record.TokenHash,
                ExpiresAt = record.ExpiresAt,
                CreatedAt = record.CreatedAt,
                LastUsedAt = record.LastUsedAt,
                RevokedAt = record.RevokedAt,
            };
    }

    public async Task TouchLastUsedAsync(Guid id, DateTimeOffset lastUsedAt, CancellationToken ct = default)
    {
        var record = await db.RefreshTokens.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (record is null)
        {
            return;
        }

        record.LastUsedAt = lastUsedAt;
        await db.SaveChangesAsync(ct);
    }

    public async Task RevokeAllForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }
}

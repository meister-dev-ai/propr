// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Repositories;

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

    /// <summary>
    ///     Returns the refresh tokens of the website sessions that are signed in at <paramref name="now" />: not revoked,
    ///     not expired, and used within the idle timeout of <paramref name="policy" />.
    /// </summary>
    /// <remarks>The extension sign-in checks the website session of an extension token with the same rule.</remarks>
    internal static IQueryable<RefreshTokenRecord> Active(IQueryable<RefreshTokenRecord> tokens, SessionPolicy policy, DateTimeOffset now)
    {
        var idleThreshold = now - policy.IdleTimeout;
        return tokens.Where(token => token.RevokedAt == null && token.ExpiresAt > now && token.LastUsedAt > idleThreshold);
    }

    public async Task<RefreshToken?> GetActiveByHashAsync(string tokenHash, CancellationToken ct = default)
    {
        var record = await Active(db.RefreshTokens, sessionPolicy, DateTimeOffset.UtcNow)
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);

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

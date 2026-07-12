// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Security;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IUserPatRepository" />.</summary>
public sealed class UserPatRepository(MeisterProPRDbContext db) : IUserPatRepository
{
    public async Task AddAsync(UserPat pat, CancellationToken ct = default)
    {
        db.UserPats.Add(
            new UserPatRecord
            {
                Id = pat.Id == Guid.Empty ? Guid.NewGuid() : pat.Id,
                UserId = pat.UserId,
                TokenHash = pat.TokenHash,
                TokenLookupHash = pat.TokenLookupHash,
                Label = pat.Label,
                ExpiresAt = pat.ExpiresAt,
                CreatedAt = pat.CreatedAt,
                LastUsedAt = pat.LastUsedAt,
                IsRevoked = pat.IsRevoked,
            });
        await db.SaveChangesAsync(ct);
    }

    public async Task<UserPat?> GetActiveByRawTokenAsync(string rawToken, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var lookupHash = PatTokenLookupHash.Compute(rawToken);

        // Fast path: a single indexed candidate by the deterministic lookup hash.
        var record = await db.UserPats
            .Where(p => p.TokenLookupHash == lookupHash && !p.IsRevoked && (p.ExpiresAt == null || p.ExpiresAt > now))
            .FirstOrDefaultAsync(ct);

        if (record is null)
        {
            // Fallback for PATs issued before the lookup hash existed: a bounded scan over only the
            // not-yet-migrated rows, backfilling the hash on a match so this set shrinks toward empty.
            var legacyCandidates = await db.UserPats
                .Where(p => p.TokenLookupHash == null && !p.IsRevoked && (p.ExpiresAt == null || p.ExpiresAt > now))
                .ToListAsync(ct);
            record = legacyCandidates.FirstOrDefault(p => BCrypt.Net.BCrypt.Verify(rawToken, p.TokenHash));
            if (record is null)
            {
                return null;
            }
        }
        else if (!BCrypt.Net.BCrypt.Verify(rawToken, record.TokenHash))
        {
            // The lookup hash only narrows candidates; the salted BCrypt hash is the authoritative check.
            return null;
        }

        record.LastUsedAt = now;
        record.TokenLookupHash = lookupHash; // idempotent on the fast path; backfills on the legacy path
        await db.SaveChangesAsync(ct);

        return MapToDomain(record);
    }

    private static UserPat MapToDomain(UserPatRecord record)
    {
        return new UserPat
        {
            Id = record.Id,
            UserId = record.UserId,
            TokenHash = record.TokenHash,
            TokenLookupHash = record.TokenLookupHash,
            Label = record.Label,
            ExpiresAt = record.ExpiresAt,
            CreatedAt = record.CreatedAt,
            LastUsedAt = record.LastUsedAt,
            IsRevoked = record.IsRevoked,
        };
    }

    public async Task<IReadOnlyList<UserPat>> ListForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var records = await db.UserPats
            .Where(p => p.UserId == userId && !p.IsRevoked && (p.ExpiresAt == null || p.ExpiresAt > now))
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);

        return records.Select(p => new UserPat
            {
                Id = p.Id,
                UserId = p.UserId,
                TokenHash = string.Empty, // never expose
                Label = p.Label,
                ExpiresAt = p.ExpiresAt,
                CreatedAt = p.CreatedAt,
                LastUsedAt = p.LastUsedAt,
                IsRevoked = p.IsRevoked,
            })
            .ToList()
            .AsReadOnly();
    }

    public async Task RevokeAsync(Guid patId, Guid userId, CancellationToken ct = default)
    {
        var record = await db.UserPats.FirstOrDefaultAsync(p => p.Id == patId && p.UserId == userId, ct);
        if (record is not null)
        {
            record.IsRevoked = true;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task RevokeAllForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await db.UserPats
            .Where(p => p.UserId == userId && !p.IsRevoked)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsRevoked, true), ct);
    }
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IUserPatRepository"/>.</summary>
public sealed class UserPatRepository(MeisterProPRDbContext db) : IUserPatRepository
{
    public async Task AddAsync(UserPat pat, CancellationToken ct = default)
    {
        db.UserPats.Add(new UserPatRecord
        {
            Id = pat.Id == Guid.Empty ? Guid.NewGuid() : pat.Id,
            UserId = pat.UserId,
            TokenHash = pat.TokenHash,
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
        // Load all non-revoked, non-expired candidates and BCrypt-verify in memory.
        var candidates = await db.UserPats
            .Where(p => !p.IsRevoked && (p.ExpiresAt == null || p.ExpiresAt > now))
            .ToListAsync(ct);

        var matched = candidates.FirstOrDefault(p =>
            BCrypt.Net.BCrypt.Verify(rawToken, p.TokenHash));

        if (matched is null)
        {
            return null;
        }

        // Update LastUsedAt
        matched.LastUsedAt = now;
        await db.SaveChangesAsync(ct);

        return new UserPat
        {
            Id = matched.Id,
            UserId = matched.UserId,
            TokenHash = matched.TokenHash,
            Label = matched.Label,
            ExpiresAt = matched.ExpiresAt,
            CreatedAt = matched.CreatedAt,
            LastUsedAt = matched.LastUsedAt,
            IsRevoked = matched.IsRevoked,
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
        }).ToList().AsReadOnly();
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

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.Security;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IUserPatRepository" />.</summary>
public sealed class UserPatRepository(MeisterProPRDbContext db, IOptions<ProvisionedUserPatOptions>? lifetimeOptions = null) : IUserPatRepository
{
    public async Task AddAsync(UserPat pat, CancellationToken ct = default)
    {
        var maximumDays = await this.GetMaximumLifetimeDaysAsync(pat.UserId, ct);
        pat.ExpiresAt = EffectiveExpiry(pat.CreatedAt, pat.ExpiresAt, maximumDays);
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

    public async Task<int?> GetMaximumLifetimeDaysAsync(Guid userId, CancellationToken ct = default)
    {
        var maximumDays = lifetimeOptions?.Value.MaximumDays;
        if (maximumDays is null)
        {
            return null;
        }

        var applies = await db.ProvisionedUsers.AnyAsync(
            user => user.UserId == userId && user.User != null && user.User.GlobalRole != AppUserRole.Admin,
            ct);
        return applies ? maximumDays : null;
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

        var maximumDays = await this.GetMaximumLifetimeDaysAsync(record.UserId, ct);
        var expiresAt = EffectiveExpiry(record.CreatedAt, record.ExpiresAt, maximumDays);
        if (expiresAt <= now)
        {
            return null;
        }

        record.LastUsedAt = now;
        record.TokenLookupHash = lookupHash; // idempotent on the fast path; backfills on the legacy path
        await db.SaveChangesAsync(ct);

        var token = MapToDomain(record);
        token.ExpiresAt = expiresAt;
        return token;
    }

    private static DateTimeOffset? EffectiveExpiry(DateTimeOffset createdAt, DateTimeOffset? expiresAt, int? maximumDays)
    {
        if (maximumDays is null)
        {
            return expiresAt;
        }

        var maximumExpiry = createdAt.AddDays(maximumDays.Value);
        return expiresAt is null || expiresAt > maximumExpiry ? maximumExpiry : expiresAt;
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
        var maximumDays = await this.GetMaximumLifetimeDaysAsync(userId, ct);
        var earliestCreation = maximumDays.HasValue ? now.AddDays(-maximumDays.Value) : (DateTimeOffset?)null;
        var records = await db.UserPats
            .Where(p => p.UserId == userId && !p.IsRevoked && (p.ExpiresAt == null || p.ExpiresAt > now))
            .Where(p => earliestCreation == null || p.CreatedAt > earliestCreation)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);

        return records.Select(p => new UserPat
            {
                Id = p.Id,
                UserId = p.UserId,
                TokenHash = string.Empty, // never expose
                Label = p.Label,
                ExpiresAt = EffectiveExpiry(p.CreatedAt, p.ExpiresAt, maximumDays),
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

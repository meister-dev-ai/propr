using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>Database-backed client registry using EF Core.</summary>
public sealed partial class DbClientRegistry(
    MeisterProPRDbContext dbContext,
    ILogger<DbClientRegistry> logger) : IClientRegistry
{
    /// <inheritdoc />
    public bool IsValidKey(string clientKey)
    {
        if (string.IsNullOrWhiteSpace(clientKey))
        {
            LogKeyNullOrWhitespace(logger);
            return false;
        }

        var now = DateTimeOffset.UtcNow;

        // Fetch active candidates; avoid BCrypt in a LINQ-to-DB query
        var candidates = dbContext.Clients
            .Where(c => c.IsActive)
            .Select(c => new { c.Key, c.KeyHash, c.KeyExpiresAt, c.PreviousKeyHash, c.PreviousKeyExpiresAt })
            .ToList();

        foreach (var c in candidates)
        {
            // BCrypt path (preferred): check current KeyHash if set and not expired
            if (!string.IsNullOrEmpty(c.KeyHash))
            {
                if (c.KeyExpiresAt is null || c.KeyExpiresAt > now)
                {
                    if (BCrypt.Net.BCrypt.Verify(clientKey, c.KeyHash))
                    {
                        return true;
                    }
                }

                // Also check PreviousKeyHash in grace period
                if (!string.IsNullOrEmpty(c.PreviousKeyHash) &&
                    c.PreviousKeyExpiresAt is not null &&
                    c.PreviousKeyExpiresAt > now)
                {
                    if (BCrypt.Net.BCrypt.Verify(clientKey, c.PreviousKeyHash))
                    {
                        return true;
                    }
                }

                // When KeyHash is set, skip legacy plaintext comparison for this record
                continue;
            }

            // Legacy plaintext fallback
            if (!string.IsNullOrEmpty(c.Key) && c.Key == clientKey)
            {
                return true;
            }
        }

        LogClientNotFound(logger);
        return false;
    }

    /// <inheritdoc />
    public async Task<Guid?> GetClientIdByKeyAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            LogKeyNullOrWhitespace(logger);
            return null;
        }

        var now = DateTimeOffset.UtcNow;

        // Load active candidates for BCrypt verification
        var candidates = await dbContext.Clients
            .Where(c => c.IsActive)
            .Select(c => new { c.Id, c.Key, c.KeyHash, c.KeyExpiresAt, c.PreviousKeyHash, c.PreviousKeyExpiresAt })
            .ToListAsync(ct);

        foreach (var c in candidates)
        {
            if (!string.IsNullOrEmpty(c.KeyHash))
            {
                if ((c.KeyExpiresAt is null || c.KeyExpiresAt > now) &&
                    BCrypt.Net.BCrypt.Verify(key, c.KeyHash))
                {
                    return c.Id;
                }

                if (!string.IsNullOrEmpty(c.PreviousKeyHash) &&
                    c.PreviousKeyExpiresAt is not null &&
                    c.PreviousKeyExpiresAt > now &&
                    BCrypt.Net.BCrypt.Verify(key, c.PreviousKeyHash))
                {
                    return c.Id;
                }

                continue;
            }

            if (!string.IsNullOrEmpty(c.Key) && c.Key == key)
            {
                return c.Id;
            }
        }

        LogClientNotFound(logger);
        return null;
    }

    /// <summary>
    ///     Rotates the client key: generates a new cryptographically-random key, BCrypt-hashes it,
    ///     promotes the current hash to PreviousKeyHash (with a grace period), and clears the legacy
    ///     plaintext key.
    /// </summary>
    /// <returns>
    ///     The new plaintext key (returned once — the caller must transmit it securely).
    ///     Returns <see langword="null"/> if the client was not found.
    /// </returns>
    public async Task<string?> RotateKeyAsync(
        Guid clientId,
        TimeSpan gracePeriod,
        CancellationToken ct = default)
    {
        using var tx = await dbContext.Database.BeginTransactionAsync(ct);

        var record = await dbContext.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (record is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;

        // Generate new key: "mpr_" prefix + 32 random bytes as hex
        var rawKey = "mpr_" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            .ToLowerInvariant();
        var newHash = BCrypt.Net.BCrypt.HashPassword(rawKey);

        // Shift current hash to previous
        record.PreviousKeyHash = record.KeyHash ?? null;
        record.PreviousKeyExpiresAt = record.PreviousKeyHash is not null ? now.Add(gracePeriod) : null;

        record.KeyHash = newHash;
        record.KeyExpiresAt = null; // new key never expires by default
        record.KeyRotatedAt = now;
        record.Key = string.Empty; // clear legacy plaintext key

        await dbContext.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return rawKey;
    }

    /// <inheritdoc />
    public async Task<Guid?> GetReviewerIdAsync(Guid clientId, CancellationToken ct = default)
    {
        return await dbContext.Clients
            .Where(c => c.Id == clientId)
            .Select(c => c.ReviewerId)
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc />
    public async Task<CommentResolutionBehavior> GetCommentResolutionBehaviorAsync(Guid clientId, CancellationToken ct = default)
    {
        return await dbContext.Clients
            .Where(c => c.Id == clientId)
            .Select(c => c.CommentResolutionBehavior)
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc />
    public async Task<string?> GetCustomSystemMessageAsync(Guid clientId, CancellationToken ct = default)
    {
        return await dbContext.Clients
            .Where(c => c.Id == clientId)
            .Select(c => c.CustomSystemMessage)
            .FirstOrDefaultAsync(ct);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Client registry: key is null or whitespace")]
    private static partial void LogKeyNullOrWhitespace(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Client registry: no active client found for key")]
    private static partial void LogClientNotFound(ILogger logger);
}

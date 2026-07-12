// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Persistence interface for refresh tokens.</summary>
public interface IRefreshTokenRepository
{
    /// <summary>Persists a new refresh token.</summary>
    Task AddAsync(RefreshToken token, CancellationToken ct = default);

    /// <summary>
    ///     Returns an active refresh token by hash, or null when it is missing, revoked, past its
    ///     absolute expiry, or idle beyond the session policy's idle timeout.
    /// </summary>
    Task<RefreshToken?> GetActiveByHashAsync(string tokenHash, CancellationToken ct = default);

    /// <summary>Advances a refresh token's last-used timestamp to keep the session within its idle window.</summary>
    Task TouchLastUsedAsync(Guid id, DateTimeOffset lastUsedAt, CancellationToken ct = default);

    /// <summary>Revokes all active refresh tokens for the given user.</summary>
    Task RevokeAllForUserAsync(Guid userId, CancellationToken ct = default);
}

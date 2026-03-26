using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Persistence interface for refresh tokens.</summary>
public interface IRefreshTokenRepository
{
    /// <summary>Persists a new refresh token.</summary>
    Task AddAsync(RefreshToken token, CancellationToken ct = default);

    /// <summary>Returns an active refresh token by hash, or null if not found or revoked/expired.</summary>
    Task<RefreshToken?> GetActiveByHashAsync(string tokenHash, CancellationToken ct = default);

    /// <summary>Revokes all active refresh tokens for the given user.</summary>
    Task RevokeAllForUserAsync(Guid userId, CancellationToken ct = default);
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Persistence interface for user-generated PATs.</summary>
public interface IUserPatRepository
{
    /// <summary>Persists a new PAT.</summary>
    Task AddAsync(UserPat pat, CancellationToken ct = default);

    /// <summary>Returns an active (non-revoked, non-expired) PAT by BCrypt-verifiable hash, or null.</summary>
    Task<UserPat?> GetActiveByRawTokenAsync(string rawToken, CancellationToken ct = default);

    /// <summary>Returns all PATs for the given user.</summary>
    Task<IReadOnlyList<UserPat>> ListForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Revokes a PAT by id.</summary>
    Task RevokeAsync(Guid patId, Guid userId, CancellationToken ct = default);

    /// <summary>Revokes all PATs for the given user (called when user is disabled).</summary>
    Task RevokeAllForUserAsync(Guid userId, CancellationToken ct = default);
}

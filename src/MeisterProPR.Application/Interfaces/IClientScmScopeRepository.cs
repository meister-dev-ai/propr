// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Persists client-scoped SCM provider scope selections.</summary>
public interface IClientScmScopeRepository
{
    /// <summary>Returns all provider scopes configured for one client connection.</summary>
    Task<IReadOnlyList<ClientScmScopeDto>> GetByConnectionIdAsync(
        Guid clientId,
        Guid connectionId,
        CancellationToken ct = default);

    /// <summary>Returns one provider scope for one client connection, or <see langword="null" /> if not found.</summary>
    Task<ClientScmScopeDto?> GetByIdAsync(
        Guid clientId,
        Guid connectionId,
        Guid scopeId,
        CancellationToken ct = default);

    /// <summary>Adds a provider scope selection for one client connection.</summary>
    Task<ClientScmScopeDto?> AddAsync(
        Guid clientId,
        Guid connectionId,
        string scopeType,
        string externalScopeId,
        string scopePath,
        string displayName,
        bool isEnabled,
        CancellationToken ct = default);

    /// <summary>Updates one provider scope selection for one client connection.</summary>
    Task<ClientScmScopeDto?> UpdateAsync(
        Guid clientId,
        Guid connectionId,
        Guid scopeId,
        string displayName,
        bool isEnabled,
        CancellationToken ct = default);

    /// <summary>Deletes one provider scope selection for one client connection.</summary>
    Task<bool> DeleteAsync(Guid clientId, Guid connectionId, Guid scopeId, CancellationToken ct = default);
}

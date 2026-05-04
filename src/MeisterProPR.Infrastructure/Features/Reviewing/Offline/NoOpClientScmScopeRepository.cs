// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     Offline placeholder for client-scoped SCM scope selections when live providers are unavailable.
/// </summary>
public sealed class NoOpClientScmScopeRepository : IClientScmScopeRepository
{
    public Task<IReadOnlyList<ClientScmScopeDto>> GetByConnectionIdAsync(
        Guid clientId,
        Guid connectionId,
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ClientScmScopeDto>>([]);
    }

    public Task<ClientScmScopeDto?> GetByIdAsync(
        Guid clientId,
        Guid connectionId,
        Guid scopeId,
        CancellationToken ct = default)
    {
        return Task.FromResult<ClientScmScopeDto?>(null);
    }

    public Task<ClientScmScopeDto?> AddAsync(
        Guid clientId,
        Guid connectionId,
        string scopeType,
        string externalScopeId,
        string scopePath,
        string displayName,
        bool isEnabled,
        CancellationToken ct = default)
    {
        throw new InvalidOperationException("Client SCM scopes are unavailable in offline review-evaluation mode.");
    }

    public Task<ClientScmScopeDto?> UpdateAsync(
        Guid clientId,
        Guid connectionId,
        Guid scopeId,
        string displayName,
        bool isEnabled,
        CancellationToken ct = default)
    {
        throw new InvalidOperationException("Client SCM scopes are unavailable in offline review-evaluation mode.");
    }

    public Task<bool> DeleteAsync(Guid clientId, Guid connectionId, Guid scopeId, CancellationToken ct = default)
    {
        throw new InvalidOperationException("Client SCM scopes are unavailable in offline review-evaluation mode.");
    }
}

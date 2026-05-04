// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     Offline placeholder for client-scoped SCM connections when live providers are out of scope.
/// </summary>
public sealed class NoOpClientScmConnectionRepository : IClientScmConnectionRepository
{
    public Task<IReadOnlyList<ClientScmConnectionDto>> GetByClientIdAsync(Guid clientId, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ClientScmConnectionDto>>([]);
    }

    public Task<ClientScmConnectionDto?> GetByIdAsync(Guid clientId, Guid connectionId, CancellationToken ct = default)
    {
        return Task.FromResult<ClientScmConnectionDto?>(null);
    }

    public Task<ClientScmConnectionCredentialDto?> GetOperationalConnectionAsync(
        Guid clientId,
        ProviderHostRef host,
        CancellationToken ct = default)
    {
        return Task.FromResult<ClientScmConnectionCredentialDto?>(null);
    }

    public Task<ClientScmConnectionDto?> AddAsync(
        Guid clientId,
        ScmProvider providerFamily,
        string hostBaseUrl,
        ScmAuthenticationKind authenticationKind,
        string? oAuthTenantId,
        string? oAuthClientId,
        string displayName,
        string secret,
        bool isActive,
        CancellationToken ct = default)
    {
        throw new InvalidOperationException("Client SCM connections are unavailable in offline review-evaluation mode.");
    }

    public Task<ClientScmConnectionDto?> UpdateAsync(
        Guid clientId,
        Guid connectionId,
        string hostBaseUrl,
        ScmAuthenticationKind authenticationKind,
        string? oAuthTenantId,
        string? oAuthClientId,
        string displayName,
        string? secret,
        bool isActive,
        CancellationToken ct = default)
    {
        throw new InvalidOperationException("Client SCM connections are unavailable in offline review-evaluation mode.");
    }

    public Task<ClientScmConnectionDto?> UpdateVerificationAsync(
        Guid clientId,
        Guid connectionId,
        string verificationStatus,
        DateTimeOffset verifiedAt,
        string? verificationError,
        CancellationToken ct = default)
    {
        throw new InvalidOperationException("Client SCM connections are unavailable in offline review-evaluation mode.");
    }

    public Task<bool> DeleteAsync(Guid clientId, Guid connectionId, CancellationToken ct = default)
    {
        throw new InvalidOperationException("Client SCM connections are unavailable in offline review-evaluation mode.");
    }
}

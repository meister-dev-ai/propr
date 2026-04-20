// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Persists client-scoped SCM provider connections.</summary>
public interface IClientScmConnectionRepository
{
    /// <summary>Returns all provider connections configured for the given client.</summary>
    Task<IReadOnlyList<ClientScmConnectionDto>> GetByClientIdAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>Returns one provider connection for the given client, or <see langword="null" /> if not found.</summary>
    Task<ClientScmConnectionDto?> GetByIdAsync(Guid clientId, Guid connectionId, CancellationToken ct = default);

    /// <summary>Returns one active provider connection plus decrypted secret material for the given client host.</summary>
    Task<ClientScmConnectionCredentialDto?> GetOperationalConnectionAsync(
        Guid clientId,
        ProviderHostRef host,
        CancellationToken ct = default);

    /// <summary>Adds a provider connection for the given client.</summary>
    Task<ClientScmConnectionDto?> AddAsync(
        Guid clientId,
        ScmProvider providerFamily,
        string hostBaseUrl,
        ScmAuthenticationKind authenticationKind,
        string? oAuthTenantId,
        string? oAuthClientId,
        string displayName,
        string secret,
        bool isActive,
        CancellationToken ct = default);

    /// <summary>Updates one provider connection for the given client.</summary>
    Task<ClientScmConnectionDto?> UpdateAsync(
        Guid clientId,
        Guid connectionId,
        string hostBaseUrl,
        ScmAuthenticationKind authenticationKind,
        string? oAuthTenantId,
        string? oAuthClientId,
        string displayName,
        string? secret,
        bool isActive,
        CancellationToken ct = default);

    /// <summary>Updates verification state for one provider connection.</summary>
    Task<ClientScmConnectionDto?> UpdateVerificationAsync(
        Guid clientId,
        Guid connectionId,
        string verificationStatus,
        DateTimeOffset verifiedAt,
        string? verificationError,
        CancellationToken ct = default);

    /// <summary>Deletes one provider connection for the given client.</summary>
    Task<bool> DeleteAsync(Guid clientId, Guid connectionId, CancellationToken ct = default);
}

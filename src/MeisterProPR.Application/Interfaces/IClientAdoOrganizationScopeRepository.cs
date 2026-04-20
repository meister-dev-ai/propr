// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Manages client-scoped Azure DevOps organization scopes used by guided configuration.
/// </summary>
public interface IClientAdoOrganizationScopeRepository
{
    /// <summary>Returns all organization scopes for the given client.</summary>
    Task<IReadOnlyList<ClientAdoOrganizationScopeDto>> GetByClientIdAsync(
        Guid clientId,
        CancellationToken ct = default);

    /// <summary>Returns one organization scope for the given client, or <see langword="null" /> if not found.</summary>
    Task<ClientAdoOrganizationScopeDto?> GetByIdAsync(Guid clientId, Guid scopeId, CancellationToken ct = default);

    /// <summary>Adds a new organization scope for the given client.</summary>
    Task<ClientAdoOrganizationScopeDto?> AddAsync(
        Guid clientId,
        string organizationUrl,
        string? displayName,
        CancellationToken ct = default);

    /// <summary>Updates the URL, display name, or enabled state for an existing scope.</summary>
    Task<ClientAdoOrganizationScopeDto?> UpdateAsync(
        Guid clientId,
        Guid scopeId,
        string organizationUrl,
        string? displayName,
        bool isEnabled,
        CancellationToken ct = default);

    /// <summary>Updates verification state for an existing scope.</summary>
    Task<ClientAdoOrganizationScopeDto?> UpdateVerificationAsync(
        Guid clientId,
        Guid scopeId,
        AdoOrganizationVerificationStatus verificationStatus,
        DateTimeOffset verifiedAt,
        string? verificationError,
        CancellationToken ct = default);

    /// <summary>Deletes an organization scope for the given client.</summary>
    Task<bool> DeleteAsync(Guid clientId, Guid scopeId, CancellationToken ct = default);
}

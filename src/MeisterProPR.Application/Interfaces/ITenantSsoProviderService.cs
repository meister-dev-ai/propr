// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Administrative CRUD operations for tenant-owned external sign-in providers.</summary>
public interface ITenantSsoProviderService
{
    Task<IReadOnlyList<TenantSsoProviderDto>> ListAsync(Guid tenantId, CancellationToken ct = default);

    Task<IReadOnlyList<TenantSsoProviderDto>> ListEnabledForTenantSlugAsync(string tenantSlug, CancellationToken ct = default);

    Task<TenantSsoProviderDto?> GetByIdAsync(Guid tenantId, Guid providerId, CancellationToken ct = default);

    Task<TenantSsoProviderDto> CreateAsync(
        Guid tenantId,
        string displayName,
        string providerKind,
        string protocolKind,
        string? issuerOrAuthorityUrl,
        string clientId,
        string? clientSecret,
        IEnumerable<string>? scopes,
        IEnumerable<string>? allowedEmailDomains,
        bool isEnabled,
        bool autoCreateUsers,
        CancellationToken ct = default);

    Task<TenantSsoProviderDto?> UpdateAsync(
        Guid tenantId,
        Guid providerId,
        string displayName,
        string providerKind,
        string protocolKind,
        string? issuerOrAuthorityUrl,
        string clientId,
        string? clientSecret,
        IEnumerable<string>? scopes,
        IEnumerable<string>? allowedEmailDomains,
        bool isEnabled,
        bool autoCreateUsers,
        CancellationToken ct = default);

    Task<bool> DeleteAsync(Guid tenantId, Guid providerId, CancellationToken ct = default);
}

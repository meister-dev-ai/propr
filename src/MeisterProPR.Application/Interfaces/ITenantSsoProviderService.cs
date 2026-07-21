// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterProPR.Application.DTOs;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Administrative CRUD operations for tenant-owned external sign-in providers.</summary>
public interface ITenantSsoProviderService
{
    /// <summary>
    ///     Lists external sign-in providers for a tenant.
    /// </summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The configured tenant SSO providers.</returns>
    Task<IReadOnlyList<TenantSsoProviderDto>> ListAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    ///     Lists enabled external sign-in providers for a tenant slug.
    /// </summary>
    /// <param name="tenantSlug">Tenant slug.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The enabled providers for the tenant.</returns>
    Task<IReadOnlyList<TenantSsoProviderDto>> ListEnabledForTenantSlugAsync(string tenantSlug, CancellationToken ct = default);

    /// <summary>
    ///     Gets a tenant SSO provider by identifier.
    /// </summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <param name="providerId">Provider identifier.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The provider when found; otherwise <c>null</c>.</returns>
    Task<TenantSsoProviderDto?> GetByIdAsync(Guid tenantId, Guid providerId, CancellationToken ct = default);

    /// <summary>
    ///     Creates an external sign-in provider for a tenant.
    /// </summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <param name="displayName">Display name shown to operators and users.</param>
    /// <param name="providerKind">Provider kind identifier.</param>
    /// <param name="protocolKind">Protocol kind identifier.</param>
    /// <param name="issuerOrAuthorityUrl">Optional issuer or authority URL.</param>
    /// <param name="clientId">Client identifier presented to the provider.</param>
    /// <param name="clientSecret">Optional client secret.</param>
    /// <param name="scopes">Optional scopes requested from the provider.</param>
    /// <param name="allowedEmailDomains">Optional allowlist of email domains.</param>
    /// <param name="isEnabled">Whether the provider starts enabled.</param>
    /// <param name="autoCreateUsers">Whether successful sign-ins may auto-create users.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The created provider.</returns>
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

    /// <summary>
    ///     Updates an external sign-in provider for a tenant.
    /// </summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <param name="providerId">Provider identifier.</param>
    /// <param name="displayName">Display name shown to operators and users.</param>
    /// <param name="providerKind">Provider kind identifier.</param>
    /// <param name="protocolKind">Protocol kind identifier.</param>
    /// <param name="issuerOrAuthorityUrl">Optional issuer or authority URL.</param>
    /// <param name="clientId">Client identifier presented to the provider.</param>
    /// <param name="clientSecret">Optional client secret.</param>
    /// <param name="scopes">Optional scopes requested from the provider.</param>
    /// <param name="allowedEmailDomains">Optional allowlist of email domains.</param>
    /// <param name="isEnabled">Whether the provider is enabled.</param>
    /// <param name="autoCreateUsers">Whether successful sign-ins may auto-create users.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The updated provider when found; otherwise <c>null</c>.</returns>
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

    /// <summary>
    ///     Deletes an external sign-in provider from a tenant.
    /// </summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <param name="providerId">Provider identifier.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns><c>true</c> when the provider was deleted; otherwise <c>false</c>.</returns>
    Task<bool> DeleteAsync(Guid tenantId, Guid providerId, CancellationToken ct = default);
}

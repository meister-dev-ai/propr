// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Administrative CRUD operations for tenant boundaries and tenant login policy.</summary>
public interface ITenantAdminService
{
    /// <summary>
    ///     Lists all tenants visible to the admin surface.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The tenant list.</returns>
    Task<IReadOnlyList<TenantDto>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    ///     Gets a tenant by identifier.
    /// </summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The tenant when found; otherwise <c>null</c>.</returns>
    Task<TenantDto?> GetByIdAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    ///     Gets a tenant by slug.
    /// </summary>
    /// <param name="tenantSlug">Tenant slug.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The tenant when found; otherwise <c>null</c>.</returns>
    Task<TenantDto?> GetBySlugAsync(string tenantSlug, CancellationToken ct = default);

    /// <summary>
    ///     Creates a tenant with the supplied login policy.
    /// </summary>
    /// <param name="slug">Tenant slug.</param>
    /// <param name="displayName">Tenant display name.</param>
    /// <param name="isActive">Whether the tenant starts active.</param>
    /// <param name="localLoginEnabled">Whether local login starts enabled.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The created tenant.</returns>
    Task<TenantDto> CreateAsync(
        string slug,
        string displayName,
        bool isActive = true,
        bool localLoginEnabled = true,
        CancellationToken ct = default);

    /// <summary>
    ///     Applies a partial update to a tenant.
    /// </summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <param name="displayName">Optional replacement display name.</param>
    /// <param name="isActive">Optional replacement active flag.</param>
    /// <param name="localLoginEnabled">Optional replacement local-login flag.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The updated tenant when found; otherwise <c>null</c>.</returns>
    Task<TenantDto?> PatchAsync(
        Guid tenantId,
        string? displayName = null,
        bool? isActive = null,
        bool? localLoginEnabled = null,
        CancellationToken ct = default);

    /// <summary>
    ///     Determines whether a tenant exists.
    /// </summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns><c>true</c> when the tenant exists; otherwise <c>false</c>.</returns>
    Task<bool> ExistsAsync(Guid tenantId, CancellationToken ct = default);
}

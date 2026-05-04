// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Administrative CRUD operations for tenant boundaries and tenant login policy.</summary>
public interface ITenantAdminService
{
    Task<IReadOnlyList<TenantDto>> GetAllAsync(CancellationToken ct = default);

    Task<TenantDto?> GetByIdAsync(Guid tenantId, CancellationToken ct = default);

    Task<TenantDto?> GetBySlugAsync(string tenantSlug, CancellationToken ct = default);

    Task<TenantDto> CreateAsync(
        string slug,
        string displayName,
        bool isActive = true,
        bool localLoginEnabled = true,
        CancellationToken ct = default);

    Task<TenantDto?> PatchAsync(
        Guid tenantId,
        string? displayName = null,
        bool? isActive = null,
        bool? localLoginEnabled = null,
        CancellationToken ct = default);

    Task<bool> ExistsAsync(Guid tenantId, CancellationToken ct = default);
}

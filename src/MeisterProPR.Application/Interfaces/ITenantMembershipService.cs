// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Administrative operations for tenant memberships and tenant-scoped roles.</summary>
public interface ITenantMembershipService
{
    Task<IReadOnlyList<TenantMembershipDto>> ListAsync(Guid tenantId, CancellationToken ct = default);

    Task<TenantMembershipDto?> GetByIdAsync(Guid tenantId, Guid membershipId, CancellationToken ct = default);

    Task<TenantMembershipDto?> GetByUserAsync(Guid tenantId, Guid userId, CancellationToken ct = default);

    Task<TenantMembershipDto> UpsertAsync(
        Guid tenantId,
        Guid userId,
        TenantRole role,
        CancellationToken ct = default);

    Task<TenantMembershipDto?> PatchAsync(
        Guid tenantId,
        Guid membershipId,
        TenantRole role,
        CancellationToken ct = default);

    Task<bool> DeleteAsync(Guid tenantId, Guid membershipId, CancellationToken ct = default);
}

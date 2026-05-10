// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Administrative operations for tenant memberships and tenant-scoped roles.</summary>
public interface ITenantMembershipService
{
    /// <summary>
    ///     Lists memberships for a tenant.
    /// </summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The tenant memberships.</returns>
    Task<IReadOnlyList<TenantMembershipDto>> ListAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    ///     Gets a tenant membership by membership identifier.
    /// </summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <param name="membershipId">Membership identifier.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The membership when found; otherwise <c>null</c>.</returns>
    Task<TenantMembershipDto?> GetByIdAsync(Guid tenantId, Guid membershipId, CancellationToken ct = default);

    /// <summary>
    ///     Gets a tenant membership by user identifier.
    /// </summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <param name="userId">User identifier.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The membership when found; otherwise <c>null</c>.</returns>
    Task<TenantMembershipDto?> GetByUserAsync(Guid tenantId, Guid userId, CancellationToken ct = default);

    /// <summary>
    ///     Creates or updates a tenant membership.
    /// </summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <param name="userId">User identifier.</param>
    /// <param name="role">Tenant role to assign.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The upserted membership.</returns>
    Task<TenantMembershipDto> UpsertAsync(
        Guid tenantId,
        Guid userId,
        TenantRole role,
        CancellationToken ct = default);

    /// <summary>
    ///     Updates the role of an existing tenant membership.
    /// </summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <param name="membershipId">Membership identifier.</param>
    /// <param name="role">Tenant role to assign.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The updated membership when found; otherwise <c>null</c>.</returns>
    Task<TenantMembershipDto?> PatchAsync(
        Guid tenantId,
        Guid membershipId,
        TenantRole role,
        CancellationToken ct = default);

    /// <summary>
    ///     Deletes a tenant membership.
    /// </summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <param name="membershipId">Membership identifier.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns><c>true</c> when a membership was deleted; otherwise <c>false</c>.</returns>
    Task<bool> DeleteAsync(Guid tenantId, Guid membershipId, CancellationToken ct = default);
}

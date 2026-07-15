// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Classifies the result of a tenant member client-access mutation.</summary>
public enum TenantMemberClientAccessOutcome
{
    /// <summary>The mutation was applied.</summary>
    Success = 0,

    /// <summary>No membership with the given identifier exists within the tenant.</summary>
    MembershipNotFound = 1,

    /// <summary>The target client does not exist within the tenant.</summary>
    ClientNotInTenant = 2,
}

/// <summary>Result of an assignment mutation, pairing the outcome with the resulting assignment when successful.</summary>
public sealed record TenantMemberClientAccessResult(
    TenantMemberClientAccessOutcome Outcome,
    TenantMemberClientAccessDto? Assignment);

/// <summary>
///     Tenant-administrator management of which tenant members may access which of the tenant's clients.
///     All operations are scoped to a single tenant and reject clients or members outside it.
/// </summary>
public interface ITenantMemberClientAccessService
{
    /// <summary>Lists the clients that belong to the tenant, for populating access pickers.</summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The tenant's clients.</returns>
    Task<IReadOnlyList<TenantClientSummaryDto>> ListTenantClientsAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Lists a member's client-access assignments within the tenant.</summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <param name="membershipId">Membership identifier of the tenant member.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The member's assignments, or <c>null</c> when the membership does not exist within the tenant.</returns>
    Task<IReadOnlyList<TenantMemberClientAccessDto>?> ListMemberAccessAsync(Guid tenantId, Guid membershipId, CancellationToken ct = default);

    /// <summary>Grants (or updates) a member's role on a client within the tenant.</summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <param name="membershipId">Membership identifier of the tenant member.</param>
    /// <param name="clientId">Client identifier; must belong to the tenant.</param>
    /// <param name="role">Client role to grant.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The mutation outcome and, on success, the resulting assignment.</returns>
    Task<TenantMemberClientAccessResult> AssignAsync(
        Guid tenantId,
        Guid membershipId,
        Guid clientId,
        ClientRole role,
        CancellationToken ct = default);

    /// <summary>Revokes a member's access to a client within the tenant (idempotent).</summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <param name="membershipId">Membership identifier of the tenant member.</param>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns><see cref="TenantMemberClientAccessOutcome.MembershipNotFound" /> when the membership is absent; otherwise <see cref="TenantMemberClientAccessOutcome.Success" />.</returns>
    Task<TenantMemberClientAccessOutcome> RemoveAsync(
        Guid tenantId,
        Guid membershipId,
        Guid clientId,
        CancellationToken ct = default);
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Persistence interface for application users.</summary>
public interface IUserRepository
{
    /// <summary>Returns a user by username (case-insensitive), or null.</summary>
    Task<AppUser?> GetByUsernameAsync(string username, CancellationToken ct = default);

    /// <summary>Returns a user by normalized email, or null when no such user exists.</summary>
    Task<AppUser?> GetByNormalizedEmailAsync(string normalizedEmail, CancellationToken ct = default);

    /// <summary>Returns a user by id, or null.</summary>
    Task<AppUser?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Returns a user with their client assignments loaded, or null.</summary>
    Task<AppUser?> GetByIdWithAssignmentsAsync(Guid id, CancellationToken ct = default);

    /// <summary>Persists a new user.</summary>
    Task AddAsync(AppUser user, CancellationToken ct = default);

    /// <summary>Sets the IsActive flag for the given user.</summary>
    Task SetActiveAsync(Guid id, bool isActive, CancellationToken ct = default);

    /// <summary>Updates the password hash for the given user.</summary>
    Task UpdatePasswordHashAsync(Guid id, string passwordHash, CancellationToken ct = default);

    /// <summary>Adds a client role assignment for a user.</summary>
    Task AddClientAssignmentAsync(UserClientRole assignment, CancellationToken ct = default);

    /// <summary>Removes a client role assignment for a user.</summary>
    Task RemoveClientAssignmentAsync(Guid userId, Guid clientId, CancellationToken ct = default);

    /// <summary>Returns all users, newest first.</summary>
    Task<IReadOnlyList<AppUser>> ListAsync(CancellationToken ct = default);

    /// <summary>Returns all client-role assignments for the given user as a dictionary keyed by client ID.</summary>
    Task<Dictionary<Guid, ClientRole>> GetUserClientRolesAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Returns a user linked to the supplied tenant-scoped external identity, or null.</summary>
    Task<AppUser?> GetByExternalIdentityAsync(
        Guid tenantId,
        Guid ssoProviderId,
        string issuer,
        string subject,
        CancellationToken ct = default);

    /// <summary>Returns the tenant membership for the supplied tenant and user, or null.</summary>
    Task<TenantMembership?> GetTenantMembershipAsync(Guid tenantId, Guid userId, CancellationToken ct = default);

    /// <summary>Returns all memberships for the supplied tenant.</summary>
    Task<IReadOnlyList<TenantMembership>> ListTenantMembershipsAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Creates or updates a tenant membership keyed by tenant and user.</summary>
    Task<TenantMembership> UpsertTenantMembershipAsync(TenantMembership membership, CancellationToken ct = default);

    /// <summary>Updates the role of an existing tenant membership, or returns null when not found.</summary>
    Task<TenantMembership?> UpdateTenantMembershipRoleAsync(
        Guid tenantId,
        Guid membershipId,
        TenantRole role,
        CancellationToken ct = default);

    /// <summary>Deletes a tenant membership when it exists.</summary>
    Task<bool> RemoveTenantMembershipAsync(Guid tenantId, Guid membershipId, CancellationToken ct = default);

    /// <summary>Persists a new tenant-scoped external identity link.</summary>
    Task AddExternalIdentityAsync(ExternalIdentity externalIdentity, CancellationToken ct = default);
}

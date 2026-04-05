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
}

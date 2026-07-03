// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json.Serialization;
using MeisterProPR.Api.Extensions;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>Admin endpoints for managing application users.</summary>
[ApiController]
public sealed class AdminUsersController(
    IUserRepository userRepository,
    IRefreshTokenRepository refreshTokenRepository,
    IUserPatRepository userPatRepository,
    IPasswordHashService passwordHashService,
    IUserAccountAuditLog auditLog) : ControllerBase
{
    /// <summary>Returns all users.</summary>
    [HttpGet("/admin/identity/users")]
    [HttpGet("/admin/users")]
    public async Task<IActionResult> ListUsers(CancellationToken ct)
    {
        var auth = AuthHelpers.RequireAdmin(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        var users = await userRepository.ListAsync(ct);
        return this.Ok(users.Select(MapToResponse));
    }

    /// <summary>Creates a new application user.</summary>
    [HttpPost("/admin/identity/users")]
    [HttpPost("/admin/users")]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest request, CancellationToken ct)
    {
        var auth = AuthHelpers.RequireAdmin(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return this.BadRequest(new { error = "Username and password are required." });
        }

        if (request.Password.Length < 8)
        {
            return this.BadRequest(new { error = "Password must be at least 8 characters." });
        }

        var existing = await userRepository.GetByUsernameAsync(request.Username, ct);
        if (existing is not null)
        {
            return this.Conflict(new { error = "A user with that username already exists." });
        }

        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Username = request.Username,
            PasswordHash = passwordHashService.Hash(request.Password),
            GlobalRole = request.GlobalRole ?? AppUserRole.User,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await userRepository.AddAsync(user, ct);
        return this.CreatedAtAction(nameof(this.ListUsers), null, MapToResponse(user));
    }

    /// <summary>
    ///     Enables or disables a user. Idempotent: confirming the current state is a no-op. Whenever the
    ///     active flag actually flips, all refresh tokens and PATs are revoked so the user must
    ///     re-authenticate. Disabling the last active global admin is refused with 409.
    /// </summary>
    [HttpPatch("/admin/identity/users/{id:guid}")]
    [HttpPatch("/admin/users/{id:guid}")]
    public async Task<IActionResult> SetUserActive(
        Guid id,
        [FromBody] SetUserActiveRequest? request,
        CancellationToken ct)
    {
        var auth = AuthHelpers.RequireAdmin(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        if (request is null)
        {
            return this.BadRequest(new { error = "A request body with an isActive value is required." });
        }

        var user = await userRepository.GetByIdAsync(id, ct);
        if (user is null)
        {
            return this.NotFound();
        }

        // Confirming the value the user already has changes nothing and leaves no audit trail.
        if (user.IsActive == request.IsActive)
        {
            return this.NoContent();
        }

        var actorUserId = AuthHelpers.GetUserId(this.HttpContext) ?? Guid.Empty;

        // Reaching here with request.IsActive == false means the user is currently active, so a global
        // admin would lose their access; refuse when no other active global admin would remain.
        if (!request.IsActive && user.GlobalRole == AppUserRole.Admin)
        {
            var activeAdmins = await userRepository.CountActiveAdminsAsync(ct);
            if (activeAdmins <= 1)
            {
                auditLog.DisableBlockedByLastAdmin(actorUserId, user.Id, user.Username);
                return this.Conflict(new { error = "Cannot disable the last active global admin." });
            }
        }

        await userRepository.SetActiveAsync(id, request.IsActive, ct);
        await refreshTokenRepository.RevokeAllForUserAsync(id, ct);
        await userPatRepository.RevokeAllForUserAsync(id, ct);

        if (request.IsActive)
        {
            auditLog.Reenabled(actorUserId, user.Id, user.Username);
        }
        else
        {
            auditLog.Disabled(actorUserId, user.Id, user.Username);
        }

        return this.NoContent();
    }

    /// <summary>
    ///     Permanently deletes a user and all their dependent records. Refuses to delete the last
    ///     active global administrator.
    /// </summary>
    [HttpDelete("/admin/identity/users/{id:guid}/permanent")]
    [HttpDelete("/admin/users/{id:guid}/permanent")]
    public async Task<IActionResult> DeleteUserPermanent(Guid id, CancellationToken ct)
    {
        var auth = AuthHelpers.RequireAdmin(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        var user = await userRepository.GetByIdAsync(id, ct);
        if (user is null)
        {
            return this.NotFound();
        }

        var actorUserId = AuthHelpers.GetUserId(this.HttpContext) ?? Guid.Empty;

        // Deleting an active admin lowers the active-admin count by one; refuse when none would remain.
        if (user.GlobalRole == AppUserRole.Admin && user.IsActive)
        {
            var activeAdmins = await userRepository.CountActiveAdminsAsync(ct);
            if (activeAdmins <= 1)
            {
                auditLog.DeleteBlockedByLastAdmin(actorUserId, user.Id, user.Username);
                return this.Conflict(new { error = "Cannot delete the last active administrator." });
            }
        }

        await userRepository.DeleteAsync(id, ct);
        auditLog.Deleted(actorUserId, user.Id, user.Username);

        return this.NoContent();
    }

    /// <summary>Returns a single user with their client role assignments.</summary>
    [HttpGet("/admin/identity/users/{id:guid}")]
    [HttpGet("/admin/users/{id:guid}")]
    public async Task<IActionResult> GetUser(Guid id, CancellationToken ct)
    {
        var auth = AuthHelpers.RequireAdmin(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        var user = await userRepository.GetByIdWithAssignmentsAsync(id, ct);
        if (user is null)
        {
            return this.NotFound();
        }

        return this.Ok(MapToDetailResponse(user));
    }

    /// <summary>Assigns a client role to a user.</summary>
    [HttpPost("/admin/identity/users/{id:guid}/clients")]
    [HttpPost("/admin/users/{id:guid}/clients")]
    public async Task<IActionResult> AssignClientRole(
        Guid id,
        [FromBody] AssignClientRoleRequest request,
        CancellationToken ct)
    {
        var auth = AuthHelpers.RequireAdmin(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        var user = await userRepository.GetByIdAsync(id, ct);
        if (user is null)
        {
            return this.NotFound();
        }

        var assignment = new UserClientRole
        {
            Id = Guid.NewGuid(),
            UserId = id,
            ClientId = request.ClientId,
            Role = request.Role,
            AssignedAt = DateTimeOffset.UtcNow,
        };

        await userRepository.AddClientAssignmentAsync(assignment, ct);
        return this.NoContent();
    }

    /// <summary>Removes a client role assignment from a user.</summary>
    [HttpDelete("/admin/identity/users/{id:guid}/clients/{clientId:guid}")]
    [HttpDelete("/admin/users/{id:guid}/clients/{clientId:guid}")]
    public async Task<IActionResult> RemoveClientRole(Guid id, Guid clientId, CancellationToken ct)
    {
        var auth = AuthHelpers.RequireAdmin(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        var user = await userRepository.GetByIdAsync(id, ct);
        if (user is null)
        {
            return this.NotFound();
        }

        await userRepository.RemoveClientAssignmentAsync(id, clientId, ct);
        return this.NoContent();
    }

    private static UserResponse MapToResponse(AppUser u)
    {
        return new UserResponse(u.Id, u.Username, u.GlobalRole, u.IsActive, u.CreatedAt);
    }

    private static UserDetailResponse MapToDetailResponse(AppUser u)
    {
        return new UserDetailResponse(
            u.Id,
            u.Username,
            u.GlobalRole,
            u.IsActive,
            u.CreatedAt,
            u.ClientAssignments.Select(a => new ClientAssignmentResponse(a.Id, a.ClientId, a.Role, a.AssignedAt))
                .ToList());
    }
}

/// <summary>Create-user request.</summary>
public sealed record CreateUserRequest(string Username, string Password, AppUserRole? GlobalRole);

/// <summary>Enable/disable request for a user.</summary>
public sealed record SetUserActiveRequest([property: JsonRequired] bool IsActive);

/// <summary>Assign-client-role request.</summary>
public sealed record AssignClientRoleRequest(
    [property: JsonRequired] Guid ClientId,
    [property: JsonRequired] ClientRole Role);

/// <summary>User response DTO.</summary>
public sealed record UserResponse(
    Guid Id,
    string Username,
    AppUserRole GlobalRole,
    bool IsActive,
    DateTimeOffset CreatedAt);

/// <summary>User detail response DTO including client role assignments.</summary>
public sealed record UserDetailResponse(
    Guid Id,
    string Username,
    AppUserRole GlobalRole,
    bool IsActive,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ClientAssignmentResponse> Assignments);

/// <summary>Client role assignment DTO.</summary>
public sealed record ClientAssignmentResponse(
    Guid AssignmentId,
    Guid ClientId,
    ClientRole Role,
    DateTimeOffset AssignedAt);

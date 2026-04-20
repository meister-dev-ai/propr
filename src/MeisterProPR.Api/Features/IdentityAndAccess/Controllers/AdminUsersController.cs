// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

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
    IPasswordHashService passwordHashService) : ControllerBase
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

    /// <summary>Disables a user and revokes all their tokens and PATs.</summary>
    [HttpDelete("/admin/identity/users/{id:guid}")]
    [HttpDelete("/admin/users/{id:guid}")]
    public async Task<IActionResult> DisableUser(Guid id, CancellationToken ct)
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

        await userRepository.SetActiveAsync(id, false, ct);
        await refreshTokenRepository.RevokeAllForUserAsync(id, ct);
        await userPatRepository.RevokeAllForUserAsync(id, ct);

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

/// <summary>Assign-client-role request.</summary>
public sealed record AssignClientRoleRequest(Guid ClientId, ClientRole Role);

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

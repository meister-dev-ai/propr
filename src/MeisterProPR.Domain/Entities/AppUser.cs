// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.Entities;

/// <summary>An application user who authenticates with username and password.</summary>
public sealed class AppUser
{
    /// <summary>Unique identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Login username (unique, case-insensitive).</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>BCrypt hash of the user's password.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Global role. Admins have access across all clients; other users are scoped via <see cref="ClientAssignments"/>.</summary>
    public AppUserRole GlobalRole { get; set; } = AppUserRole.User;

    /// <summary>Whether the account is active. Disabled users cannot authenticate.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>When the account was created.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Per-client role assignments. Only used when <see cref="GlobalRole"/> is <see cref="AppUserRole.User"/>.</summary>
    public ICollection<UserClientRole> ClientAssignments { get; } = [];

    /// <summary>Personal access tokens issued by this user.</summary>
    public ICollection<UserPat> Pats { get; } = [];

    /// <summary>Active refresh tokens for this user.</summary>
    public ICollection<RefreshToken> RefreshTokens { get; } = [];
}

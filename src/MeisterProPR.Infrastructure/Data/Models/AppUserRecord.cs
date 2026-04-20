// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>EF persistence model for <see cref="Domain.Entities.AppUser" />.</summary>
public sealed class AppUserRecord
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public AppUserRole GlobalRole { get; set; } = AppUserRole.User;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<UserClientRoleRecord> ClientAssignments { get; set; } = [];
    public ICollection<UserPatRecord> Pats { get; set; } = [];
    public ICollection<RefreshTokenRecord> RefreshTokens { get; set; } = [];
}

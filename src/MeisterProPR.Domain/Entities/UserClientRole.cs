// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.Entities;

/// <summary>Assigns a user a role within a specific client.</summary>
public sealed class UserClientRole
{
    /// <summary>Unique identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>User who holds this role assignment.</summary>
    public Guid UserId { get; set; }

    /// <summary>Client to which this role applies.</summary>
    public Guid ClientId { get; set; }

    /// <summary>Role the user holds for this client.</summary>
    public ClientRole Role { get; set; }

    /// <summary>When this assignment was created.</summary>
    public DateTimeOffset AssignedAt { get; set; } = DateTimeOffset.UtcNow;
}

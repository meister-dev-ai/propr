// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.Entities;

/// <summary>Grants a user tenant-scoped access and administration rights.</summary>
public sealed class TenantMembership
{
    /// <summary>Unique identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Tenant this membership belongs to.</summary>
    public Guid TenantId { get; set; }

    /// <summary>User who holds the membership.</summary>
    public Guid UserId { get; set; }

    /// <summary>Tenant-scoped role held by the user.</summary>
    public TenantRole Role { get; set; } = TenantRole.TenantUser;

    /// <summary>When the membership was assigned.</summary>
    public DateTimeOffset AssignedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the membership was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

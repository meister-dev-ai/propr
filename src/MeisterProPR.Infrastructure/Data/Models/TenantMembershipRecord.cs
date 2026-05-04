// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>EF persistence model for a tenant membership.</summary>
public sealed class TenantMembershipRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public TenantRole Role { get; set; } = TenantRole.TenantUser;
    public DateTimeOffset AssignedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public TenantRecord? Tenant { get; set; }
    public AppUserRecord? User { get; set; }
}

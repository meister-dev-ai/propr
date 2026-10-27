// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Infrastructure.Data.Models;

/// <summary>EF persistence model for the elevation of a platform administrator into one tenant or one client.</summary>
public sealed class PlatformAdminElevationRecord
{
    public Guid Id { get; set; }

    /// <summary>The platform administrator. Null after the account was deleted; the name stays in <see cref="AdministratorName" />.</summary>
    public Guid? AdministratorUserId { get; set; }

    public string AdministratorName { get; set; } = string.Empty;

    public Guid TenantId { get; set; }

    /// <summary>The one client the elevation covers, or null for every client of the tenant.</summary>
    public Guid? ClientId { get; set; }

    public string Reason { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When the elevation ended early, or the expiry ProPR recorded for it. Null while nothing ended it.</summary>
    public DateTimeOffset? EndedAt { get; set; }

    public Guid? EndedByUserId { get; set; }

    public string? EndedByName { get; set; }

    /// <summary>Changed on every update, so two requests that end the same elevation record one end.</summary>
    public Guid ConcurrencyStamp { get; set; }

    public TenantRecord? Tenant { get; set; }

    public ClientRecord? Client { get; set; }

    public AppUserRecord? AdministratorUser { get; set; }
}

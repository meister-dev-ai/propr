// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>EF persistence model for append-only tenant administration audit history.</summary>
public sealed class TenantAuditEntryRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? ActorUserId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public DateTimeOffset OccurredAt { get; set; }

    public TenantRecord? Tenant { get; set; }
    public AppUserRecord? ActorUser { get; set; }
}

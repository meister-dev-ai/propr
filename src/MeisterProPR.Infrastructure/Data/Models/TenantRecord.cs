// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>EF persistence model for the tenant boundary.</summary>
public sealed class TenantRecord
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public bool LocalLoginEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<ClientRecord> Clients { get; set; } = [];
    public ICollection<TenantMembershipRecord> Memberships { get; set; } = [];
    public ICollection<TenantSsoProviderRecord> SsoProviders { get; set; } = [];
    public ICollection<ExternalIdentityRecord> ExternalIdentities { get; set; } = [];
    public ICollection<TenantAuditEntryRecord> AuditEntries { get; set; } = [];
}

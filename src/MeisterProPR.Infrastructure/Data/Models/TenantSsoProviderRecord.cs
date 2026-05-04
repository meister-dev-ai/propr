// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>EF persistence model for a tenant-owned external sign-in provider.</summary>
public sealed class TenantSsoProviderRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string ProviderKind { get; set; } = string.Empty;
    public string ProtocolKind { get; set; } = string.Empty;
    public string? IssuerOrAuthorityUrl { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string? ClientSecretProtected { get; set; }
    public string[] Scopes { get; set; } = [];
    public string[] AllowedEmailDomains { get; set; } = [];
    public bool IsEnabled { get; set; } = true;
    public bool AutoCreateUsers { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public TenantRecord? Tenant { get; set; }
    public ICollection<ExternalIdentityRecord> ExternalIdentities { get; set; } = [];
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Entities;

/// <summary>Top-level tenant boundary for sign-in policy, memberships, and administration.</summary>
public sealed class Tenant
{
    /// <summary>Unique identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Stable URL-safe slug used to resolve tenant sign-in context.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Display name shown in administration and sign-in flows.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Whether the tenant is active and can be used for sign-in.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Whether local username and password sign-in remains enabled for this tenant.</summary>
    public bool LocalLoginEnabled { get; set; } = true;

    /// <summary>When the tenant was created.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the tenant was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Tenant-scoped user memberships.</summary>
    public ICollection<TenantMembership> Memberships { get; } = [];

    /// <summary>Tenant-owned external sign-in providers.</summary>
    public ICollection<TenantSsoProvider> SsoProviders { get; } = [];
}

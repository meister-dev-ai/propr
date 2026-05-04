// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Entities;

/// <summary>Tenant-owned external sign-in configuration.</summary>
public sealed class TenantSsoProvider
{
    /// <summary>Unique identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Tenant that owns this provider configuration.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Display name shown to tenant users during sign-in.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Provider family identifier, such as Entra ID, Google, or GitHub.</summary>
    public string ProviderKind { get; set; } = string.Empty;

    /// <summary>Protocol kind used by the provider, such as OIDC or OAuth 2.0.</summary>
    public string ProtocolKind { get; set; } = string.Empty;

    /// <summary>Issuer or authority URL when the provider requires one.</summary>
    public string? IssuerOrAuthorityUrl { get; set; }

    /// <summary>Provider-issued client identifier.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Protected provider client secret.</summary>
    public string? ClientSecretProtected { get; set; }

    /// <summary>Requested scopes for the provider integration.</summary>
    public ICollection<string> Scopes { get; } = [];

    /// <summary>Allowed email domains for first-time external sign-in.</summary>
    public ICollection<string> AllowedEmailDomains { get; } = [];

    /// <summary>Whether the provider is enabled for sign-in.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Whether the provider may auto-create new users on first successful sign-in.</summary>
    public bool AutoCreateUsers { get; set; } = true;

    /// <summary>When the provider was created.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the provider was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Entities;

/// <summary>Tenant-scoped mapping from an external identity to an internal user.</summary>
public sealed class ExternalIdentity
{
    /// <summary>Unique identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Tenant this identity link belongs to.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Internal application user linked to the external identity.</summary>
    public Guid UserId { get; set; }

    /// <summary>Provider configuration that issued this identity.</summary>
    public Guid SsoProviderId { get; set; }

    /// <summary>External issuer identifier.</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>Stable external subject identifier.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Email reported by the external provider.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Whether the external provider verified the reported email.</summary>
    public bool EmailVerified { get; set; }

    /// <summary>When the identity link was created.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the linked external identity last signed in successfully.</summary>
    public DateTimeOffset? LastSignInAt { get; set; }
}

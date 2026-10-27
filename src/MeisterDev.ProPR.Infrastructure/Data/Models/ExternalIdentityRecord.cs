// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Data.Models;

/// <summary>
///     EF persistence model for an external identity link. A link made through a tenant identity provider carries
///     the tenant and the provider. A link made through the identity provider for platform administrators carries
///     neither, because that provider is configured for the installation and has no row of its own.
/// </summary>
public sealed class ExternalIdentityRecord
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid? SsoProviderId { get; set; }
    public string Issuer { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool EmailVerified { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastSignInAt { get; set; }

    public TenantRecord? Tenant { get; set; }
    public AppUserRecord? User { get; set; }
    public TenantSsoProviderRecord? SsoProvider { get; set; }
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>EF persistence model for a client-owned SCM provider connection.</summary>
public sealed class ClientScmConnectionRecord
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public ScmProvider Provider { get; set; }
    public string HostBaseUrl { get; set; } = string.Empty;
    public ScmAuthenticationKind AuthenticationKind { get; set; }
    public string? OAuthTenantId { get; set; }
    public string? OAuthClientId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string EncryptedSecretMaterial { get; set; } = string.Empty;
    public string VerificationStatus { get; set; } = "unknown";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastVerifiedAt { get; set; }
    public string? LastVerificationError { get; set; }
    public string? LastVerificationFailureCategory { get; set; }

    public ClientRecord? Client { get; set; }
    public ICollection<ClientScmScopeRecord> Scopes { get; set; } = [];
    public ICollection<ClientReviewerIdentityRecord> ReviewerIdentities { get; set; } = [];
}

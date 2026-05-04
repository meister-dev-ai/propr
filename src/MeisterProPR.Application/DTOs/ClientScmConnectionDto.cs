// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs;

/// <summary>Client-scoped SCM provider connection metadata returned by admin APIs.</summary>
public sealed record ClientScmConnectionDto(
    Guid Id,
    Guid ClientId,
    ScmProvider ProviderFamily,
    string HostBaseUrl,
    ScmAuthenticationKind AuthenticationKind,
    string? OAuthTenantId,
    string? OAuthClientId,
    string DisplayName,
    bool IsActive,
    string VerificationStatus,
    DateTimeOffset? LastVerifiedAt,
    string? LastVerificationError,
    string? LastVerificationFailureCategory,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ProviderConnectionReadinessLevel ReadinessLevel = ProviderConnectionReadinessLevel.Unknown,
    string? ReadinessReason = null,
    string HostVariant = "unknown",
    IReadOnlyList<string>? MissingReadinessCriteria = null)
{
    /// <summary>Initializes a new instance of the <see cref="ClientScmConnectionDto" /> record with required parameters only.</summary>
    public ClientScmConnectionDto(
        Guid id,
        Guid clientId,
        ScmProvider providerFamily,
        string hostBaseUrl,
        ScmAuthenticationKind authenticationKind,
        string displayName,
        bool isActive,
        string verificationStatus,
        DateTimeOffset? lastVerifiedAt,
        string? lastVerificationError,
        string? lastVerificationFailureCategory,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
        : this(
            id,
            clientId,
            providerFamily,
            hostBaseUrl,
            authenticationKind,
            null,
            null,
            displayName,
            isActive,
            verificationStatus,
            lastVerifiedAt,
            lastVerificationError,
            lastVerificationFailureCategory,
            createdAt,
            updatedAt)
    {
    }
}

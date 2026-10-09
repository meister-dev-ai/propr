// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Api.Features.Clients.Contracts;

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
    IReadOnlyList<string>? MissingReadinessCriteria = null,
    long? GitHubAppId = null,
    long? GitHubAppInstallationId = null,
    string? UserName = null,
    bool StoreThreads = false,
    bool StoreDiffs = false,
    int? RetentionDays = null)
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
            updatedAt,
            GitHubAppId: null,
            GitHubAppInstallationId: null)
    {
    }

    /// <summary>Projects internal coordinates to the existing HTTP field names.</summary>
    public static ClientScmConnectionDto FromApplication(MeisterDev.ProPR.Application.DTOs.ClientScmConnectionDto value) =>
        new(
            value.Id, value.ClientId, value.ProviderFamily, value.HostBaseUrl, value.AuthenticationKind,
            value.OAuthTenantId, value.OAuthClientId, value.DisplayName, value.IsActive, value.VerificationStatus,
            value.LastVerifiedAt, value.LastVerificationError, value.LastVerificationFailureCategory, value.CreatedAt,
            value.UpdatedAt, value.ReadinessLevel, value.ReadinessReason, value.HostVariant, value.MissingReadinessCriteria,
            value.AppId, value.InstallationId, value.UserName, value.StoreThreads, value.StoreDiffs, value.RetentionDays);
}

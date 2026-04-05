// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     Client-scoped allowlist record for an Azure DevOps organization that can be used in guided configuration.
/// </summary>
public sealed class ClientAdoOrganizationScope
{
    public ClientAdoOrganizationScope(
        Guid id,
        Guid clientId,
        string organizationUrl,
        string? displayName,
        bool isEnabled,
        AdoOrganizationVerificationStatus verificationStatus,
        DateTimeOffset? lastVerifiedAt = null,
        string? lastVerificationError = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must not be empty.", nameof(id));
        }

        if (clientId == Guid.Empty)
        {
            throw new ArgumentException("ClientId must not be empty.", nameof(clientId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(organizationUrl);

        this.Id = id;
        this.ClientId = clientId;
        this.OrganizationUrl = organizationUrl.Trim();
        this.DisplayName = NormalizeOptional(displayName);
        this.IsEnabled = isEnabled;
        this.VerificationStatus = verificationStatus;
        this.LastVerifiedAt = lastVerifiedAt;
        this.LastVerificationError = NormalizeOptional(lastVerificationError);
        this.CreatedAt = DateTimeOffset.UtcNow;
        this.UpdatedAt = this.CreatedAt;
    }

    private ClientAdoOrganizationScope()
    {
    }

    public Guid Id { get; private set; }

    public Guid ClientId { get; private set; }

    public string OrganizationUrl { get; private set; } = string.Empty;

    public string? DisplayName { get; private set; }

    public bool IsEnabled { get; private set; }

    public AdoOrganizationVerificationStatus VerificationStatus { get; private set; }

    public DateTimeOffset? LastVerifiedAt { get; private set; }

    public string? LastVerificationError { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public void UpdateDisplay(string organizationUrl, string? displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationUrl);

        this.OrganizationUrl = organizationUrl.Trim();
        this.DisplayName = NormalizeOptional(displayName);
        this.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void RecordVerification(
        AdoOrganizationVerificationStatus verificationStatus,
        DateTimeOffset verifiedAt,
        string? verificationError = null)
    {
        this.VerificationStatus = verificationStatus;
        this.LastVerifiedAt = verifiedAt;
        this.LastVerificationError = NormalizeOptional(verificationError);
        this.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void SetEnabled(bool isEnabled)
    {
        this.IsEnabled = isEnabled;
        this.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

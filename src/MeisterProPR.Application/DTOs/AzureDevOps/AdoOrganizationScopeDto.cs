// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs.AzureDevOps;

/// <summary>
///     Client-scoped organization option and verification state used by guided Azure DevOps configuration.
/// </summary>
public sealed record ClientAdoOrganizationScopeDto(
    Guid Id,
    Guid ClientId,
    string OrganizationUrl,
    string? DisplayName,
    bool IsEnabled,
    AdoOrganizationVerificationStatus VerificationStatus,
    DateTimeOffset? LastVerifiedAt,
    string? LastVerificationError,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

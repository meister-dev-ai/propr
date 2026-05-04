// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.DTOs;

/// <summary>Tenant-owned external sign-in provider metadata returned by admin APIs.</summary>
public sealed record TenantSsoProviderDto(
    Guid Id,
    Guid TenantId,
    string DisplayName,
    string ProviderKind,
    string ProtocolKind,
    string? IssuerOrAuthorityUrl,
    string ClientId,
    bool SecretConfigured,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> AllowedEmailDomains,
    bool IsEnabled,
    bool AutoCreateUsers,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

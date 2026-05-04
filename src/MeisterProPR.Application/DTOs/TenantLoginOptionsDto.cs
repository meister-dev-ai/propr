// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.DTOs;

/// <summary>Tenant-scoped login options shown before a tenant user signs in.</summary>
public sealed record TenantLoginOptionsDto(
    string TenantSlug,
    bool LocalLoginEnabled,
    IReadOnlyList<TenantLoginProviderDto> Providers);

/// <summary>Public metadata for an enabled tenant-owned sign-in provider.</summary>
public sealed record TenantLoginProviderDto(
    Guid ProviderId,
    string DisplayName,
    string ProviderKind,
    string ProviderLabel);

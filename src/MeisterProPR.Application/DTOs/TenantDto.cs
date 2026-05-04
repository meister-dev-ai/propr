// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.DTOs;

/// <summary>Tenant boundary data returned by administration and tenant-auth flows.</summary>
public sealed record TenantDto(
    Guid Id,
    string Slug,
    string DisplayName,
    bool IsActive,
    bool LocalLoginEnabled,
    bool IsEditable,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs;

/// <summary>Tenant membership data returned by administration flows.</summary>
public sealed record TenantMembershipDto(
    Guid Id,
    Guid TenantId,
    Guid UserId,
    string Username,
    string? Email,
    bool UserIsActive,
    TenantRole Role,
    DateTimeOffset AssignedAt,
    DateTimeOffset UpdatedAt);

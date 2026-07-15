// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs;

/// <summary>A single client-access assignment held by a tenant member within a tenant.</summary>
public sealed record TenantMemberClientAccessDto(
    Guid ClientId,
    string ClientDisplayName,
    ClientRole Role,
    DateTimeOffset AssignedAt);

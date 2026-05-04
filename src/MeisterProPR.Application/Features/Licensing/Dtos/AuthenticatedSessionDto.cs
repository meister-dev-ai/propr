// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Licensing.Models;

namespace MeisterProPR.Application.Features.Licensing.Dtos;

/// <summary>Authenticated session snapshot shared with the admin UI after sign-in.</summary>
public sealed record AuthenticatedSessionDto(
    string GlobalRole,
    Dictionary<string, int> ClientRoles,
    Dictionary<string, int> TenantRoles,
    bool HasLocalPassword,
    InstallationEdition Edition,
    IReadOnlyList<PremiumCapabilityDto> Capabilities);

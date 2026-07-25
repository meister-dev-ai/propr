// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;

namespace MeisterDev.ProPR.Application.Features.Licensing.Dtos;

/// <summary>Public authentication bootstrap information for the current installation edition.</summary>
public sealed record AuthOptionsDto(
    InstallationEdition Edition,
    IReadOnlyList<string> AvailableSignInMethods,
    IReadOnlyList<PremiumCapabilityDto> Capabilities,
    string? PublicBaseUrl = null);

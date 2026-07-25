// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;

namespace MeisterDev.ProPR.Application.Features.Licensing.Dtos;

/// <summary>API-facing representation of one premium capability's effective state.</summary>
public sealed record PremiumCapabilityDto(
    string Key,
    string DisplayName,
    bool RequiresCommercial,
    bool DefaultWhenCommercial,
    PremiumCapabilityOverrideState OverrideState,
    bool IsAvailable,
    string? Message);

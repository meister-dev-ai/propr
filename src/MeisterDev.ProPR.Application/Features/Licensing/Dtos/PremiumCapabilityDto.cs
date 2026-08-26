// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;

namespace MeisterDev.ProPR.Application.Features.Licensing.Dtos;

/// <summary>
///     API-facing representation of one premium capability's effective state. <c>reason</c> is set exactly when
///     <c>isAvailable</c> is false.
/// </summary>
public sealed record PremiumCapabilityDto(
    string Key,
    string DisplayName,
    bool RequiresCommercial,
    PremiumCapabilityOverrideState OverrideState,
    bool IsAvailable,
    string? Message,
    PremiumCapabilityUnavailableReason? Reason = null);

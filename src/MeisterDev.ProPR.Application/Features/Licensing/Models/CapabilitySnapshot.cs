// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

namespace MeisterDev.ProPR.Application.Features.Licensing.Models;

/// <summary>
///     Resolved effective state for one premium capability. <see cref="Reason" /> is set exactly when
///     <see cref="IsAvailable" /> is <see langword="false" />, so a caller can branch on why rather than parse
///     <see cref="Message" />.
/// </summary>
public sealed record CapabilitySnapshot(
    string Key,
    string DisplayName,
    bool RequiresCommercial,
    PremiumCapabilityOverrideState OverrideState,
    bool IsAvailable,
    string? Message,
    PremiumCapabilityUnavailableReason? Reason = null);

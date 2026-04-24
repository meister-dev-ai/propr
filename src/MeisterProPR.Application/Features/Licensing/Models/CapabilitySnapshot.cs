// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Licensing.Models;

/// <summary>Resolved effective state for one premium capability.</summary>
public sealed record CapabilitySnapshot(
    string Key,
    string DisplayName,
    bool RequiresCommercial,
    bool DefaultWhenCommercial,
    PremiumCapabilityOverrideState OverrideState,
    bool IsAvailable,
    string? Message);

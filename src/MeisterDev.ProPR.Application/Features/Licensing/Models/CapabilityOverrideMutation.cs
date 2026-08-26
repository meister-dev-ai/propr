// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

namespace MeisterDev.ProPR.Application.Features.Licensing.Models;

/// <summary>Requested override change for a specific premium capability.</summary>
public sealed record CapabilityOverrideMutation(string Key, PremiumCapabilityOverrideState OverrideState);

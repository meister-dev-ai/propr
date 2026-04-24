// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Licensing.Models;

/// <summary>Optional installation override for one premium capability.</summary>
public enum PremiumCapabilityOverrideState
{
    /// <summary>Use the capability's default availability for the current edition.</summary>
    Default = 0,

    /// <summary>Force the capability to be enabled.</summary>
    Enabled = 1,

    /// <summary>Force the capability to be disabled.</summary>
    Disabled = 2,
}

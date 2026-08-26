// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Licensing.Models;

/// <summary>
///     Optional installation override for one premium capability. An override can only take a capability away:
///     what an installation is entitled to comes from the license it has activated, so there is no state that
///     turns a capability on.
/// </summary>
public enum PremiumCapabilityOverrideState
{
    /// <summary>Leave the capability's availability to the license.</summary>
    Default = 0,

    /// <summary>
    ///     Keep the capability unavailable even while the license covers it. The number stays 2 because stored
    ///     rows already carry it; 1 is the removed enable state and is not reused.
    /// </summary>
    Disabled = 2,
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

namespace MeisterDev.ProPR.Application.Features.Licensing.Models;

/// <summary>Persisted installation-wide licensing state plus per-capability overrides.</summary>
public sealed record InstallationLicensingPolicy(
    InstallationEdition Edition,
    DateTimeOffset? ActivatedAt,
    Guid? ActivatedByUserId,
    DateTimeOffset UpdatedAt,
    Guid? UpdatedByUserId,
    IReadOnlyDictionary<string, PremiumCapabilityOverrideState> CapabilityOverrides)
{
    /// <summary>
    ///     The number the removed enable state was stored as. It is not reused, so no state this build defines
    ///     carries it and a row still holding it can only have come from an older build.
    /// </summary>
    private const int RemovedEnableState = 1;

    /// <summary>
    ///     Returns the stored override for a capability, or <see cref="PremiumCapabilityOverrideState.Default" />
    ///     when no override is stored for it.
    ///     <para>
    ///         A stored value this build does not define reads as
    ///         <see cref="PremiumCapabilityOverrideState.Disabled" />. An override can only take a capability
    ///         away, so every state a later build can add is one that withholds something, and a row written by
    ///         that later build is one an older build has to be able to read. Falling back to
    ///         <see cref="PremiumCapabilityOverrideState.Default" /> would leave the capability to the license,
    ///         which is to say available, and re-enable something an administrator had turned off.
    ///     </para>
    ///     <para>
    ///         The exception is <see cref="RemovedEnableState" />, which reads as no override. It named a state
    ///         that made a capability available rather than withholding one, so denying on it would take away
    ///         what the row was written to grant.
    ///     </para>
    /// </summary>
    public PremiumCapabilityOverrideState GetOverrideState(string capabilityKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityKey);

        if (!this.CapabilityOverrides.TryGetValue(capabilityKey, out var state))
        {
            return PremiumCapabilityOverrideState.Default;
        }

        if (Enum.IsDefined(state))
        {
            return state;
        }

        return (int)state == RemovedEnableState
            ? PremiumCapabilityOverrideState.Default
            : PremiumCapabilityOverrideState.Disabled;
    }
}

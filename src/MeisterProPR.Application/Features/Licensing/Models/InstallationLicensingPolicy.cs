// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Licensing.Models;

/// <summary>Persisted installation-wide licensing state plus per-capability overrides.</summary>
public sealed record InstallationLicensingPolicy(
    InstallationEdition Edition,
    DateTimeOffset? ActivatedAt,
    Guid? ActivatedByUserId,
    DateTimeOffset UpdatedAt,
    Guid? UpdatedByUserId,
    IReadOnlyDictionary<string, PremiumCapabilityOverrideState> CapabilityOverrides)
{
    /// <summary>Returns the stored override for a capability, or <see cref="PremiumCapabilityOverrideState.Default" />.</summary>
    public PremiumCapabilityOverrideState GetOverrideState(string capabilityKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityKey);

        return this.CapabilityOverrides.TryGetValue(capabilityKey, out var state)
            ? state
            : PremiumCapabilityOverrideState.Default;
    }
}

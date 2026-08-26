// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Models;

namespace MeisterDev.ProPR.Application.Features.Licensing.Ports;

/// <summary>Persistence boundary for installation-wide edition and premium-capability overrides.</summary>
public interface ILicensingPolicyStore
{
    /// <summary>Loads the current installation licensing policy, seeding Community defaults when needed.</summary>
    Task<InstallationLicensingPolicy> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Persists the requested capability overrides. The stored edition is not written here: it follows from
    ///     the license the installation has activated, not from an override request.
    /// </summary>
    Task<InstallationLicensingPolicy> UpdateAsync(
        IReadOnlyCollection<CapabilityOverrideMutation> capabilityOverrides,
        Guid? actorUserId,
        CancellationToken cancellationToken = default);
}

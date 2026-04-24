// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Licensing.Models;

namespace MeisterProPR.Application.Features.Licensing.Ports;

/// <summary>Persistence boundary for installation-wide edition and premium-capability overrides.</summary>
public interface ILicensingPolicyStore
{
    /// <summary>Loads the current installation licensing policy, seeding Community defaults when needed.</summary>
    Task<InstallationLicensingPolicy> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists a new edition plus any requested capability overrides.</summary>
    Task<InstallationLicensingPolicy> UpdateAsync(
        InstallationEdition edition,
        IReadOnlyCollection<CapabilityOverrideMutation> capabilityOverrides,
        Guid? actorUserId,
        CancellationToken cancellationToken = default);
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Licensing.Dtos;
using MeisterProPR.Application.Features.Licensing.Models;

namespace MeisterProPR.Application.Features.Licensing.Ports;

/// <summary>Resolves effective premium capability state for backend enforcement and UI contracts.</summary>
public interface ILicensingCapabilityService
{
    /// <summary>Returns the full installation-wide licensing summary.</summary>
    Task<LicensingSummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns public sign-in options for the current edition and capability state.</summary>
    Task<AuthOptionsDto> GetAuthOptionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the resolved snapshot for one capability key.</summary>
    Task<CapabilitySnapshot> GetCapabilityAsync(string capabilityKey, CancellationToken cancellationToken = default);

    /// <summary>Returns <see langword="true" /> when the supplied capability is currently available.</summary>
    ValueTask<bool> IsEnabledAsync(string capabilityKey, CancellationToken cancellationToken = default);

    /// <summary>Updates the installation edition and capability overrides, then returns the resulting summary.</summary>
    Task<LicensingSummaryDto> UpdateAsync(
        InstallationEdition edition,
        IReadOnlyCollection<CapabilityOverrideMutation> capabilityOverrides,
        Guid? actorUserId,
        CancellationToken cancellationToken = default);
}

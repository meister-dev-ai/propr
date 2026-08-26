// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Dtos;
using MeisterDev.ProPR.Application.Features.Licensing.Models;

namespace MeisterDev.ProPR.Application.Features.Licensing.Ports;

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

    /// <summary>Updates the installation-wide capability overrides, then returns the resulting summary.</summary>
    Task<LicensingSummaryDto> UpdateAsync(
        IReadOnlyCollection<CapabilityOverrideMutation> capabilityOverrides,
        Guid? actorUserId,
        CancellationToken cancellationToken = default);
}

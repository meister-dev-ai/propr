// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Dtos;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;

namespace MeisterDev.ProPR.Application.Features.Licensing.Commands.UpdateLicensing;

/// <summary>Updates the installation-wide capability overrides and returns the resulting summary.</summary>
public sealed class UpdateLicensingHandler(ILicensingCapabilityService licensingCapabilityService)
{
    /// <summary>Applies the requested capability overrides.</summary>
    public Task<LicensingSummaryDto> HandleAsync(
        UpdateLicensingCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        return licensingCapabilityService.UpdateAsync(
            command.CapabilityOverrides,
            command.ActorUserId,
            cancellationToken);
    }
}

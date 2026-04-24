// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Licensing.Dtos;
using MeisterProPR.Application.Features.Licensing.Ports;

namespace MeisterProPR.Application.Features.Licensing.Commands.UpdateLicensing;

/// <summary>Updates installation-wide licensing state and returns the resulting summary.</summary>
public sealed class UpdateLicensingHandler(ILicensingCapabilityService licensingCapabilityService)
{
    /// <summary>Applies the requested installation edition and capability overrides.</summary>
    public Task<LicensingSummaryDto> HandleAsync(
        UpdateLicensingCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        return licensingCapabilityService.UpdateAsync(
            command.Edition,
            command.CapabilityOverrides,
            command.ActorUserId,
            cancellationToken);
    }
}

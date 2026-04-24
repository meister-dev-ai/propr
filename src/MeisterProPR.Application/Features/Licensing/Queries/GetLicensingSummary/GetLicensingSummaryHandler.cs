// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Licensing.Dtos;
using MeisterProPR.Application.Features.Licensing.Ports;

namespace MeisterProPR.Application.Features.Licensing.Queries.GetLicensingSummary;

/// <summary>Loads the installation-wide licensing summary for admin and session consumers.</summary>
public sealed class GetLicensingSummaryHandler(ILicensingCapabilityService licensingCapabilityService)
{
    /// <summary>Returns the current installation licensing summary.</summary>
    public Task<LicensingSummaryDto> HandleAsync(
        GetLicensingSummaryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        return licensingCapabilityService.GetSummaryAsync(cancellationToken);
    }
}

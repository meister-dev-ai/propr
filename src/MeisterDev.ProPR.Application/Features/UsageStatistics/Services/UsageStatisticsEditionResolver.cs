// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Models;

namespace MeisterDev.ProPR.Application.Features.UsageStatistics.Services;

/// <summary>
///     Derives the reported edition from whether a license is installed.
///     <para>
///         The edition is read from the licensing state rather than configured separately, so the reported
///         value cannot disagree with the license the installation runs under.
///     </para>
/// </summary>
public sealed class UsageStatisticsEditionResolver(ILicensingCapabilityService? licensingCapabilityService = null)
{
    /// <summary>Returns the edition to report.</summary>
    public async Task<UsageStatisticsEdition> ResolveAsync(CancellationToken cancellationToken = default)
    {
        if (licensingCapabilityService is null)
        {
            // No licensing module means no database-backed installation state, and therefore no license.
            return UsageStatisticsEdition.Community;
        }

        var summary = await licensingCapabilityService.GetSummaryAsync(cancellationToken);
        return Map(summary.Edition);
    }

    /// <summary>
    ///     Maps an installation edition onto the two values the wire carries.
    ///     <para>
    ///         The switch is exhaustive. A licensing state added later for a trial, an expiry or a grace period
    ///         is reported as community until the mapping is updated, so no new state is reported by default.
    ///     </para>
    /// </summary>
    internal static UsageStatisticsEdition Map(InstallationEdition edition)
    {
        return edition switch
        {
            InstallationEdition.Commercial => UsageStatisticsEdition.Commercial,
            InstallationEdition.Community => UsageStatisticsEdition.Community,
            _ => UsageStatisticsEdition.Community,
        };
    }
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.UsageStatistics.Models;

namespace MeisterDev.ProPR.Application.Features.UsageStatistics.Ports;

/// <summary>Counts the installation's own activity for one observation window.</summary>
public interface IUsageStatisticsCountSource
{
    /// <summary>
    ///     Counts activity between <paramref name="windowStart" /> and <paramref name="windowEnd" />.
    ///     <para>
    ///         The counts are instance-wide and not scoped by tenant or client, so the payload reports the
    ///         installation's total activity and no breakdown of how that work is divided inside it.
    ///     </para>
    /// </summary>
    Task<UsageStatisticsCounts> CountAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken = default);
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.UsageStatistics.Models;

namespace MeisterDev.ProPR.Application.Features.UsageStatistics.Ports;

/// <summary>Delivers one snapshot to the vendor receiver.</summary>
public interface IUsageStatisticsPingClient
{
    /// <summary>
    ///     Sends <paramref name="snapshot" /> and returns what the receiver answered.
    ///     <para>
    ///         Implementations own the whole outbound path, so no caller above this port opens a socket or
    ///         resolves a name. The disabled and pre-consent states perform no network activity because they
    ///         never reach this port.
    ///     </para>
    /// </summary>
    Task<UsageStatisticsSendOutcome> SendAsync(
        UsageStatisticsSnapshot snapshot,
        CancellationToken cancellationToken = default);
}

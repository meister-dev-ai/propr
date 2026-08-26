// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.UsageStatistics.Models;

/// <summary>
///     The edition an installation reports in its anonymous usage statistics.
///     <para>
///         This is a separate wire type rather than a reuse of the licensing enum. The licensing layer
///         distinguishes states this wire does not carry, such as a term approaching its end and the grace window
///         after one has ended, and a state added later for a trial would be another. A separate enum forces
///         every one of them to be mapped onto these two values before it can be sent.
///     </para>
/// </summary>
public enum UsageStatisticsEdition
{
    /// <summary>The installation is not entitled to the commercial capabilities.</summary>
    Community = 0,

    /// <summary>The installation is entitled to what its license names, which includes the grace window.</summary>
    Commercial = 1,
}

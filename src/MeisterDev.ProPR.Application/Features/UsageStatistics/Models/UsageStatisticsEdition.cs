// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.UsageStatistics.Models;

/// <summary>
///     The edition an installation reports in its anonymous usage statistics.
///     <para>
///         This is a separate wire type rather than a reuse of the licensing enum. A licensing state added
///         later for a trial, an expiry or a grace period must not be reported, and a separate enum forces such
///         a state to be mapped onto one of these two values before it can be sent.
///     </para>
/// </summary>
public enum UsageStatisticsEdition
{
    /// <summary>No commercial license is installed.</summary>
    Community = 0,

    /// <summary>A commercial license is installed.</summary>
    Commercial = 1,
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.UsageStatistics.Models;

namespace MeisterDev.ProPR.Application.Features.UsageStatistics.Dtos;

/// <summary>
///     What the last successful ping reported about newer releases and security advisories.
///     <para>
///         Absent information renders nothing. An installation that has never pinged, or that has usage
///         statistics off, shows no badge and no error.
///     </para>
/// </summary>
/// <param name="CurrentVersion">The running release version.</param>
/// <param name="LatestVersion">The newest release the receiver reported, when it reported one.</param>
/// <param name="UpdateAvailable">Whether the running version differs from the newest one reported.</param>
/// <param name="Advisories">Security advisories the receiver reported for the running version.</param>
/// <param name="ReceivedAt">When this information arrived.</param>
public sealed record UsageStatisticsUpdateStatusDto(
    string CurrentVersion,
    string? LatestVersion,
    bool UpdateAvailable,
    IReadOnlyList<ProductAdvisory> Advisories,
    DateTimeOffset? ReceivedAt);

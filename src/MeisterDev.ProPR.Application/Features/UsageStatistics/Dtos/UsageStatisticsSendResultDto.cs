// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.UsageStatistics.Models;

namespace MeisterDev.ProPR.Application.Features.UsageStatistics.Dtos;

/// <summary>
///     The result of an administrator's request to send now.
///     <para>
///         The decision is returned because the settings page cannot otherwise tell the cases apart where
///         nothing was sent: switched off, awaiting the consent notice, and already sent today all leave the
///         same visible state.
///     </para>
/// </summary>
/// <param name="Decision">Why the cycle did or did not reach the network.</param>
/// <param name="Settings">The state after the attempt, including the last outcome.</param>
public sealed record UsageStatisticsSendResultDto(
    UsageStatisticsSendDecision Decision,
    UsageStatisticsSettingsDto Settings);

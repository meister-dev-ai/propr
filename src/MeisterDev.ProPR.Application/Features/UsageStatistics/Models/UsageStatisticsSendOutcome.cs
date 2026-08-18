// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.UsageStatistics.Models;

/// <summary>
///     The result of one send attempt, as it is stored for the settings page.
///     <para>
///         A failure is recorded and not retried. There is no queue: the next daily cycle sends a fresh
///         snapshot, and an undelivered snapshot has no further effect.
///     </para>
/// </summary>
/// <param name="AttemptedAt">When the attempt ran.</param>
/// <param name="Succeeded">Whether the receiver accepted the snapshot.</param>
/// <param name="Detail">A short description shown on the settings page.</param>
/// <param name="Response">The receiver's response, when there was one.</param>
public sealed record UsageStatisticsSendOutcome(
    DateTimeOffset AttemptedAt,
    bool Succeeded,
    string Detail,
    UsageStatisticsPingResponse? Response);

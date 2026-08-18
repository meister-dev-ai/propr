// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.UsageStatistics.Models;

/// <summary>
///     The outcome of one send cycle, and the timestamp the loop schedules from next.
///     <para>
///         The timestamp is returned with the decision so the loop does not re-read it from the store. After a
///         send the value is the current time whether or not storing the outcome succeeded; treating a failed
///         store as an unspent day would send another snapshot on the next cycle.
///     </para>
/// </summary>
/// <param name="Decision">Why the cycle did or did not reach the network.</param>
/// <param name="LastAttemptAt">When an attempt last happened, as of the end of this cycle.</param>
public sealed record UsageStatisticsCycleResult(
    UsageStatisticsSendDecision Decision,
    DateTimeOffset? LastAttemptAt);

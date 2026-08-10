// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     The fleet as the offline harness sees it: empty, always.
///     <para>
///         The harness has no database, and the real monitor reads one. Without this the worker's
///         per-tick <c>GetService&lt;IRunnerFleetMonitor&gt;()</c> would try to construct the Postgres-backed
///         monitor and throw on every cycle, turning "no runners configured" into a broken harness.
///     </para>
/// </summary>
public sealed class OfflineRunnerFleetMonitor : IRunnerFleetMonitor
{
    private static readonly RunnerFleetStatus Empty =
        new(ReviewExecutionMode.InProcess, 0, null, new HashSet<Guid>());

    /// <inheritdoc />
    public Task<RunnerFleetStatus> GetStatusAsync(CancellationToken ct = default)
    {
        return Task.FromResult(Empty);
    }
}

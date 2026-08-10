// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Runner;

/// <summary>
///     What this runner would tell an orchestrator probe.
///     <para>
///         Deliberately generous about what counts as healthy. A runner that cannot reach its control
///         plane, or whose contract has been refused, is still a healthy process doing exactly what it
///         should: retrying and saying why. Reporting those as unhealthy would have an orchestrator restart
///         a container whose problem is on the other end of the network, replacing a diagnosable host with
///         a crash loop.
///     </para>
/// </summary>
public sealed class RunnerHealthState
{
    private readonly Lock _gate = new();
    private Status _current = Status.Starting;
    private string? _detail;

    /// <summary>What the runner is doing.</summary>
    public enum Status
    {
        /// <summary>Started, has not yet asked for work.</summary>
        Starting = 0,

        /// <summary>Asking for work and finding none.</summary>
        Idle,

        /// <summary>Running at least one review.</summary>
        Working,

        /// <summary>Cannot reach the control plane, and retrying.</summary>
        Disconnected,

        /// <summary>The control plane is draining, so no new work is being issued to anybody.</summary>
        Draining,

        /// <summary>Reachable, but this runner is being refused work for a reason an operator must fix.</summary>
        Refused,
    }

    /// <summary>Records what the loop last saw.</summary>
    /// <param name="status">The state.</param>
    /// <param name="detail">Operator-readable detail, when there is any.</param>
    public void Report(Status status, string? detail)
    {
        lock (this._gate)
        {
            this._current = status;
            this._detail = detail;
        }
    }

    /// <summary>The current state and its detail.</summary>
    public (Status Current, string? Detail) Read()
    {
        lock (this._gate)
        {
            return (this._current, this._detail);
        }
    }
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Decides where reviews run, and says so out loud when nothing is running them.
///     <para>
///         One predicate, in one place. "Is there an active runner" is asked by the worker before it claims
///         anything and by the stall check that explains an idle queue, and two implementations of it would
///         eventually disagree about whether the installation is currently distributed.
///     </para>
/// </summary>
public interface IRunnerFleetMonitor
{
    /// <summary>The current fleet state and what it means for where reviews execute.</summary>
    /// <param name="ct">The cancellation token.</param>
    Task<RunnerFleetStatus> GetStatusAsync(CancellationToken ct = default);
}

/// <summary>Where reviews execute right now, and why.</summary>
public enum ReviewExecutionMode
{
    /// <summary>No runner is active, so the control plane executes reviews itself, as it always has.</summary>
    InProcess = 0,

    /// <summary>
    ///     At least one runner is active, so the control plane executes nothing and waits for runners to
    ///     take the work. There is deliberately no automatic fallback: a silent one would void the isolation
    ///     promise on exactly the installations relying on it, and would do so without telling anybody.
    /// </summary>
    RunnersOnly,
}

/// <summary>
///     The fleet as the control plane currently sees it.
/// </summary>
/// <param name="Mode">Where reviews execute, taken across the whole installation.</param>
/// <param name="ActiveRunnerCount">How many runners were heard from inside the active window.</param>
/// <param name="Stall">The stall condition, or null when the queue is not stalled.</param>
/// <param name="ClientsWithActiveRunner">
///     Clients an active runner is eligible to take work for. Empty when nothing is active.
/// </param>
public sealed record RunnerFleetStatus(
    ReviewExecutionMode Mode,
    int ActiveRunnerCount,
    QueueStallCondition? Stall,
    IReadOnlySet<Guid>? ClientsWithActiveRunner = null)
{
    /// <summary>Whether any runner at all is taking work right now.</summary>
    public bool AnyRunnerActive => this.Mode != ReviewExecutionMode.InProcess;

    /// <summary>
    ///     Whether this control plane may execute the given client's work itself.
    ///     <para>
    ///         Per client rather than installation-wide, because runners are scoped and the installation is
    ///         not. One tenant running runners must not stop every other tenant's reviews: those jobs can
    ///         never be offered to a runner outside their tenant, so suppressing them in the control plane
    ///         too would leave them pending forever while the fleet looks perfectly healthy.
    ///     </para>
    /// </summary>
    /// <param name="clientId">The client whose job is being considered.</param>
    public bool MayExecuteInProcess(Guid clientId)
    {
        return this.Mode == ReviewExecutionMode.InProcess
               || this.ClientsWithActiveRunner?.Contains(clientId) != true;
    }
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Entities;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Chooses which pending jobs may be offered to a given runner.
///     <para>
///         Only reads. Winning a job is still the conditional claim every other holder uses, so two runners
///         offered the same candidate resolve it the same way two control-plane replicas do, and no central
///         view of who is busy has to be kept correct.
///     </para>
/// </summary>
public interface IRunnerLeaseOfferStore
{
    /// <summary>
    ///     Pending jobs this runner is allowed to be offered, in the order they should be tried.
    ///     <para>
    ///         Ordering is fair across clients rather than oldest-first across the installation: one client
    ///         that queues two hundred pull requests would otherwise hold every runner until it drained, and
    ///         every other client would wait behind it. Each client's oldest job is offered before any
    ///         client's second-oldest.
    ///     </para>
    /// </summary>
    /// <param name="tenantId">The runner's tenant. A runner is never offered work outside it.</param>
    /// <param name="clientScope">
    ///     The clients the server stamped onto the runner. Empty means every client in the tenant, which is
    ///     what an unrestricted enrollment gets.
    /// </param>
    /// <param name="runnerTags">Tags the runner declares. A job is eligible when the runner declares all of the ones its client requires.</param>
    /// <param name="limit">How many candidates to consider. Bounds the work one offer costs.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<IReadOnlyList<ReviewJob>> GetOfferCandidatesAsync(
        Guid tenantId,
        IReadOnlyList<Guid> clientScope,
        IReadOnlyList<string> runnerTags,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    ///     Pending jobs whose client requires a tag that no currently active runner declares.
    ///     <para>
    ///         These would otherwise sit pending forever and look identical to a queue that is merely busy.
    ///         Naming them is what turns a routing mistake into something an operator can see.
    ///     </para>
    /// </summary>
    /// <param name="activeSince">A runner counts as active when it was last heard from at or after this instant.</param>
    /// <param name="limit">How many to report. Bounds the query for an installation with a large stuck queue.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<IReadOnlyList<UnroutableJob>> GetUnroutableJobsAsync(
        DateTimeOffset activeSince,
        int limit,
        CancellationToken ct = default);

    /// <summary>How many runners currently hold at least one lease. One runner running several jobs counts once.</summary>
    /// <param name="ct">The cancellation token.</param>
    Task<int> CountRunnersHoldingLeasesAsync(CancellationToken ct = default);

    /// <summary>
    ///     The fleet as one consistent snapshot: how many runners are enrolled, how many are active, and
    ///     which clients an active runner could actually take work for.
    ///     <para>
    ///         One statement rather than three, because the three are compared against each other. Read
    ///         separately they can describe different instants, and a runner enrolling between two of them
    ///         is enough to make the control plane conclude the fleet is empty while it is not.
    ///     </para>
    ///     <para>
    ///         Client eligibility is by tenant and stamped scope, deliberately not by tag. A job whose tags
    ///         no runner declares must stay pending and show as unroutable: executing it in the control
    ///         plane instead would quietly break the isolation promise on exactly the installation that
    ///         asked for it.
    ///     </para>
    /// </summary>
    /// <param name="activeSince">A runner counts as active when it was last heard from at or after this instant.</param>
    /// <param name="oldestSupportedContractVersion">Lowest contract version this control plane can serve.</param>
    /// <param name="newestSupportedContractVersion">Highest contract version this control plane can serve.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<RunnerFleetSnapshot> GetFleetSnapshotAsync(
        DateTimeOffset activeSince,
        int oldestSupportedContractVersion,
        int newestSupportedContractVersion,
        CancellationToken ct = default);
}

/// <summary>
///     One consistent read of the fleet.
/// </summary>
/// <param name="RegisteredRunnerCount">Enrolled runners, whether or not they are heartbeating.</param>
/// <param name="ActiveRunnerCount">Runners that are enrolled, contract-compatible, and heartbeating.</param>
/// <param name="ClientsWithActiveRunner">
///     Clients an active runner is eligible to take work for, by tenant and stamped scope. A client absent
///     from this set has no runner that could ever be offered its jobs, whatever the fleet's size.
/// </param>
public sealed record RunnerFleetSnapshot(
    int RegisteredRunnerCount,
    int ActiveRunnerCount,
    IReadOnlySet<Guid> ClientsWithActiveRunner);

/// <summary>A pending job no active runner can take, and the tags that make it so.</summary>
/// <param name="JobId">The job.</param>
/// <param name="ClientId">The client whose requirement cannot be met.</param>
/// <param name="RequiredTags">What the client requires.</param>
/// <param name="PendingSince">When the job was submitted, so an operator can see how long it has waited.</param>
public sealed record UnroutableJob(
    Guid JobId,
    Guid ClientId,
    IReadOnlyList<string> RequiredTags,
    DateTimeOffset PendingSince);

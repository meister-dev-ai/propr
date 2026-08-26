// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Runner.Contracts;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>What a runner tells the control plane when it asks for work.</summary>
/// <param name="RunnerId">The authenticated runner. Never taken from the request body.</param>
/// <param name="FreeSlots">
///     How many more jobs this runner can take. The asking side owns capacity: a busy runner simply does not
///     ask, which is what makes pull-based dispatch work without a central view of who is free.
/// </param>
/// <param name="ContractVersion">The contract version the runner speaks.</param>
public sealed record RunnerLeaseRequest(Guid RunnerId, int FreeSlots, int ContractVersion);

/// <summary>Why a lease was not granted. An empty queue and a refusal are deliberately different answers.</summary>
public enum RunnerLeaseRefusal
{
    /// <summary>A lease was granted.</summary>
    None = 0,

    /// <summary>Nothing pending matches this runner's scope, tags, and the fair ordering.</summary>
    NoMatchingWork,

    /// <summary>The runner reported no free slot, so it should not have asked.</summary>
    NoFreeCapacity,

    /// <summary>The runner's registration is not usable: revoked, unknown, or expired.</summary>
    RegistrationNotUsable,

    /// <summary>The runner speaks a contract version this control plane cannot serve.</summary>
    UnsupportedContractVersion,

    /// <summary>Distributed execution is not licensed on this installation.</summary>
    NotLicensed,

    /// <summary>
    ///     The installation is already running as many reviews at once as its concurrent-review ceiling
    ///     allows. Held apart from <see cref="NoMatchingWork" /> because the queue is not empty: the work
    ///     exists and is waiting for a running review to finish, which is a different thing for an
    ///     operator to see.
    /// </summary>
    ConcurrencyCeilingReached,

    /// <summary>The control plane is draining and is deliberately handing out no new work.</summary>
    Draining,
}

/// <summary>
///     The answer to a lease request: a manifest, or a typed reason there is none.
///     <para>
///         The reason matters as much as the manifest. An operator looking at an idle queue needs to tell
///         "nothing to do" apart from "out of slots" apart from "no runner declares the tag this client
///         needs", and a single null answer collapses all three into a mystery.
///     </para>
/// </summary>
public sealed record RunnerLeaseOffer
{
    private RunnerLeaseOffer(RunnerJobManifest? manifest, RunnerLeaseRefusal refusal, string? detail, int? ceiling = null)
    {
        this.Manifest = manifest;
        this.Refusal = refusal;
        this.Detail = detail;
        this.Ceiling = ceiling;
    }

    /// <summary>The manifest for the job this runner now holds, or null when none was granted.</summary>
    public RunnerJobManifest? Manifest { get; }

    /// <summary>Why no lease was granted, or <see cref="RunnerLeaseRefusal.None" /> when one was.</summary>
    public RunnerLeaseRefusal Refusal { get; }

    /// <summary>Operator-readable detail for a refusal, when there is more to say than its name.</summary>
    public string? Detail { get; }

    /// <summary>
    ///     The concurrent-review ceiling a <see cref="RunnerLeaseRefusal.ConcurrencyCeilingReached" />
    ///     refusal was made against, and null for every other answer. Carried as a number because the
    ///     detail states the same ceiling in prose, and a metric label cannot use prose.
    /// </summary>
    public int? Ceiling { get; }

    /// <summary>Whether a job was leased.</summary>
    public bool Granted => this.Manifest is not null;

    /// <summary>A granted lease and the manifest to execute it under.</summary>
    /// <param name="manifest">The resolved manifest.</param>
    public static RunnerLeaseOffer Grant(RunnerJobManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new RunnerLeaseOffer(manifest, RunnerLeaseRefusal.None, null);
    }

    /// <summary>No lease, and why.</summary>
    /// <param name="refusal">The typed reason.</param>
    /// <param name="detail">Optional operator-readable detail.</param>
    public static RunnerLeaseOffer Refuse(RunnerLeaseRefusal refusal, string? detail = null)
    {
        // None means a lease was granted, so a refusal carrying it is neither granted nor refused. The
        // controller's fallback would answer 204 and the caller would read an invalid state as an empty
        // queue, which is the one outcome that prompts no investigation.
        if (refusal == RunnerLeaseRefusal.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(refusal),
                "A refusal cannot carry RunnerLeaseRefusal.None, which means a lease was granted.");
        }

        return new RunnerLeaseOffer(null, refusal, detail);
    }

    /// <summary>
    ///     No lease, because the installation is at its concurrent-review ceiling.
    ///     <para>
    ///         Built here rather than through <see cref="Refuse" /> so the ceiling reaches the caller as a
    ///         number. The controller counts the refusal against that number, which is how an operator sees
    ///         which limit an idle fleet is waiting on.
    ///     </para>
    /// </summary>
    /// <param name="ceiling">The ceiling that was reached.</param>
    /// <param name="detail">Operator-readable detail naming the ceiling and where it comes from.</param>
    public static RunnerLeaseOffer RefuseAtConcurrencyCeiling(int ceiling, string? detail = null)
    {
        return new RunnerLeaseOffer(null, RunnerLeaseRefusal.ConcurrencyCeilingReached, detail, ceiling);
    }
}

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

    /// <summary>Every entitled runner slot is already held.</summary>
    SlotLimitReached,

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
    private RunnerLeaseOffer(RunnerJobManifest? manifest, RunnerLeaseRefusal refusal, string? detail)
    {
        this.Manifest = manifest;
        this.Refusal = refusal;
        this.Detail = detail;
    }

    /// <summary>The manifest for the job this runner now holds, or null when none was granted.</summary>
    public RunnerJobManifest? Manifest { get; }

    /// <summary>Why no lease was granted, or <see cref="RunnerLeaseRefusal.None" /> when one was.</summary>
    public RunnerLeaseRefusal Refusal { get; }

    /// <summary>Operator-readable detail for a refusal, when there is more to say than its name.</summary>
    public string? Detail { get; }

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
        // queue, which is the one outcome nobody would investigate.
        if (refusal == RunnerLeaseRefusal.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(refusal),
                "A refusal cannot carry RunnerLeaseRefusal.None, which means a lease was granted.");
        }

        return new RunnerLeaseOffer(null, refusal, detail);
    }
}

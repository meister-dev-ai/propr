// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>Answers a runner asking for work with a manifest or a typed reason there is none.</summary>
public interface IRunnerLeaseOfferService
{
    /// <summary>Offers this runner the highest-priority job it is allowed to take, if there is one.</summary>
    /// <param name="request">Who is asking, how much room it has, and which contract it speaks.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<RunnerLeaseOffer> OfferAsync(RunnerLeaseRequest request, CancellationToken ct = default);
}

/// <summary>
///     Decides whether another runner may hold a lease at all.
///     <para>
///         Optional by design: an installation with no entitlement enforcement registers nothing here and
///         leasing is bounded only by the work available. Enforcement lives on the control plane rather than
///         in the runner, because a check inside the artifact a customer hosts is the easiest one to remove.
///     </para>
/// </summary>
public interface IRunnerSlotEntitlement
{
    /// <summary>Whether this runner may take a lease right now.</summary>
    /// <param name="runnerId">The runner asking. A runner already holding leases consumes one slot however many jobs it runs.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<RunnerSlotAdmission> AdmitAsync(Guid runnerId, CancellationToken ct = default);
}

/// <summary>The entitlement's answer: admitted, or a typed refusal an operator can act on.</summary>
/// <param name="Refusal">The reason, or <see cref="RunnerLeaseRefusal.None" /> when admitted.</param>
/// <param name="Detail">Operator-readable detail when there is more to say than the reason's name.</param>
public sealed record RunnerSlotAdmission(RunnerLeaseRefusal Refusal, string? Detail = null)
{
    /// <summary>Admitted.</summary>
    public static RunnerSlotAdmission Admitted { get; } = new(RunnerLeaseRefusal.None);
}

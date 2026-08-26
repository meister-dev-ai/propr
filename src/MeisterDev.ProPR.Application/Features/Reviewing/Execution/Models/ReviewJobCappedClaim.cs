// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>What a claim bounded by a concurrency cap did.</summary>
public enum ReviewJobCappedClaimOutcome
{
    /// <summary>
    ///     The installation is already running as many reviews as the cap allows. No other job can be
    ///     claimed either until one of them finishes.
    /// </summary>
    AtCapacity = 0,

    /// <summary>
    ///     There was capacity, but this particular job was no longer claimable, usually because another
    ///     party claimed it first. The rest of the queue is unaffected.
    /// </summary>
    NotClaimable = 1,

    /// <summary>The job was claimed and the lease is carried on the result.</summary>
    Granted = 2,
}

/// <summary>
///     The result of a claim that is granted only while the installation is below its concurrency cap.
///     A refusal names which of the two reasons applied, because a caller scanning a queue has to react
///     differently to each: at the cap it stops, whereas a job someone else took only costs it that candidate.
///     <para>
///         An at-capacity refusal carries the cap it was measured against and the count the store observed, so
///         a caller can report both numbers.
///     </para>
///     <para>
///         Only the factory methods below construct instances, so no caller can build one whose payload
///         contradicts its outcome.
///     </para>
/// </summary>
public sealed record ReviewJobCappedClaim
{
    private ReviewJobCappedClaim()
    {
    }

    /// <summary>Which of the three cases occurred.</summary>
    public ReviewJobCappedClaimOutcome Outcome { get; private init; }

    /// <summary>
    ///     The granted lease. Present only when the outcome is
    ///     <see cref="ReviewJobCappedClaimOutcome.Granted" />.
    /// </summary>
    public ReviewJobLease? Lease { get; private init; }

    /// <summary>
    ///     The cap the claim was measured against. Present only when the outcome is
    ///     <see cref="ReviewJobCappedClaimOutcome.AtCapacity" />.
    /// </summary>
    public int? Cap { get; private init; }

    /// <summary>
    ///     How many jobs the store counted as executing inside the admission. Null when the claimant never
    ///     reached the admission and therefore counted nothing.
    /// </summary>
    public int? ProcessingCount { get; private init; }

    /// <summary>This job is no longer claimable, but the cap left room for another one.</summary>
    public static ReviewJobCappedClaim NotClaimable { get; } =
        new() { Outcome = ReviewJobCappedClaimOutcome.NotClaimable };

    /// <summary>The cap is full, so nothing can be claimed right now.</summary>
    /// <param name="cap">How many jobs the cap allows to execute at once.</param>
    /// <param name="processingCount">
    ///     How many jobs the store observed as executing, or null when the claimant did not reach the
    ///     admission that counts them.
    /// </param>
    public static ReviewJobCappedClaim AtCapacity(int cap, int? processingCount = null)
    {
        return new ReviewJobCappedClaim
        {
            Outcome = ReviewJobCappedClaimOutcome.AtCapacity,
            Cap = cap,
            ProcessingCount = processingCount,
        };
    }

    /// <summary>Carries a granted lease.</summary>
    /// <param name="lease">The lease the claim stamped.</param>
    public static ReviewJobCappedClaim Granted(ReviewJobLease lease)
    {
        return new ReviewJobCappedClaim
        {
            Outcome = ReviewJobCappedClaimOutcome.Granted,
            Lease = lease,
        };
    }
}

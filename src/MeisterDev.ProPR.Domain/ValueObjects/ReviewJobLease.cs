// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Domain.ValueObjects;

/// <summary>
///     A held claim on a review job: who holds it, which generation they hold, and when it expires.
///     Carried by the holder for the duration of execution and presented on every write it makes against
///     the job, so a holder that lost the job to a reclaim is recognised by its stale generation instead of
///     being allowed to overwrite the work of whoever took over.
/// </summary>
/// <param name="JobId">The leased review job.</param>
/// <param name="Owner">Identity of the holder, as stamped by the claim.</param>
/// <param name="Generation">The generation this holder was granted.</param>
/// <param name="ExpiresAt">When the lease expires unless renewed, as read from the database clock.</param>
public sealed record ReviewJobLease(Guid JobId, string Owner, int Generation, DateTimeOffset ExpiresAt);

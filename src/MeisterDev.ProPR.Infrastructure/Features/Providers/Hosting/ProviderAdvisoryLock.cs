// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

/// <summary>
///     Serializes the writes that enforce a per-family cap, across every replica of the deployment.
/// </summary>
/// <remarks>
///     <para>
///         A cap counted and then written is not a cap: every concurrent writer reads the same count, every one
///         of them is under the limit, and every one of them writes. A unique index cannot express these caps
///         either, because what is bounded is how many rows a family holds and not which rows they are.
///     </para>
///     <para>
///         The lock is held by the transaction and released when it ends, including when the connection is lost,
///         so a replica that dies mid-write does not block the rest. It is keyed on the family and on which cap
///         is being enforced, so two families never wait on each other and the two caps do not share a queue.
///     </para>
/// </remarks>
internal static class ProviderAdvisoryLock
{
    /// <summary>The cap on the unclaimed entries one family holds.</summary>
    public const int KeyedEntries = 1;

    /// <summary>The cap on the resources one family leases.</summary>
    public const int ResourceLeases = 2;

    /// <summary>
    ///     Takes the lock for one family until the caller's transaction ends, waiting for whoever holds it.
    /// </summary>
    /// <remarks>
    ///     <c>hashtext</c> supplies the second half of the key. A collision between two families puts one behind
    ///     the other for the length of one write, which costs a wait and changes no answer.
    /// </remarks>
    /// <param name="db">The context whose transaction holds the lock. It has to be in one.</param>
    /// <param name="scope">Which cap is being enforced, from the constants on this type.</param>
    /// <param name="addInKey">The family the cap is stated for.</param>
    /// <param name="ct">Cancels the wait.</param>
    public static Task TakeAsync(MeisterProPRDbContext db, int scope, string addInKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        return db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock({0}, hashtext({1}))",
            [scope, addInKey],
            ct);
    }
}

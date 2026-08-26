// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MeisterDev.ProPR.Infrastructure.Features.Licensing.Persistence;

/// <summary>
///     Admits one more client or enrolled runner against its ceiling, and an existing one against the ceiling
///     it now has to fit under, using PostgreSQL to decide which of two simultaneous creations gets the last
///     place.
///     <para>
///         The serialization is a transaction-scoped advisory lock, one per quota, taken before the count is
///         read. The count is only meaningful under that lock: the installation runs at READ COMMITTED, so a
///         count taken after the lock is granted includes the row the previous holder committed, while two
///         creations counting without the lock both read the state from before either wrote. Row locking does
///         not help, because the two rows differ.
///     </para>
///     <para>
///         The lock is released when the transaction ends, so the transaction spans the count and the caller's
///         write. It is opened on the scoped context injected into the request, which is the context the
///         caller saves through, and handed to the admission for the caller to commit. A transaction the caller opened first
///         stays the caller's: the decision is made inside it and the caller's own commit releases the lock.
///     </para>
///     <para>
///         The lock names follow the scheme the concurrent-review admission uses, so every admission on the
///         installation draws its lock name from one namespace, and a new quota does not reuse the name of an
///         existing one.
///     </para>
/// </summary>
/// <param name="dbContext">The context the count is taken on and the caller's creation saves through.</param>
/// <param name="limits">Resolves the ceiling the count is compared against.</param>
/// <param name="timeProvider">The clock a runner credential is judged expired against.</param>
/// <param name="licensingCapabilityService">
///     Answers whether multi-tenancy is available, which decides which clients the installation holds.
/// </param>
public sealed class PostgresStockQuotaGate(
    MeisterProPRDbContext dbContext,
    ILicenseLimitResolver limits,
    TimeProvider timeProvider,
    ILicensingCapabilityService? licensingCapabilityService = null) : IStockQuotaGate
{
    private const string TakeQuotaLockSql = "SELECT pg_advisory_xact_lock(hashtextextended({0}, 0))";

    /// <inheritdoc />
    public Task<StockQuotaAdmission> AdmitOneAsync(
        LicenseLimitKey key,
        CancellationToken cancellationToken = default) =>
        this.AdmitAsync(key, subjectIsCounted: false, cancellationToken);

    /// <inheritdoc />
    public Task<StockQuotaAdmission> AdmitExistingAsync(
        LicenseLimitKey key,
        CancellationToken cancellationToken = default) =>
        this.AdmitAsync(key, subjectIsCounted: true, cancellationToken);

    /// <summary>
    ///     Decides one admission. Both questions read the same count under the same lock and differ only in
    ///     where the count is refused, so they share everything up to that comparison.
    /// </summary>
    /// <param name="key">The stock quota to decide against.</param>
    /// <param name="subjectIsCounted">
    ///     Whether what is being admitted is already part of the count. A creation is not, so the count must
    ///     stay below the ceiling to leave room for it; an existing resource is, so the count only has to be
    ///     within the ceiling.
    /// </param>
    /// <param name="cancellationToken">Cancels the decision.</param>
    /// <returns>The admission.</returns>
    private async Task<StockQuotaAdmission> AdmitAsync(
        LicenseLimitKey key,
        bool subjectIsCounted,
        CancellationToken cancellationToken)
    {
        var quota = this.QuotaFor(key);
        var limit = await limits.ResolveAsync(key, cancellationToken).ConfigureAwait(false);

        if (limit.Ceiling == LicenseLimitCeiling.Unlimited)
        {
            // No lock is taken and nothing is counted. Serializing creations installation-wide would enforce
            // nothing, and a count would be compared against nothing.
            return StockQuotaAdmission.Admitted(limit);
        }

        if (limit.Count is not { } ceiling)
        {
            // Clients and runners resolve to an unlimited ceiling or to a count in LicenseLimitResolver. The
            // unmetered ceiling is the answer for distinct authors within a month, which no row count bounds.
            // Reported as a state fault rather than a bad argument: the key names a quota this gate admits, and
            // no other key the caller could pass would produce a countable ceiling for it.
            throw new InvalidOperationException($"The ceiling resolved for {key} is {limit.Ceiling}, which no row count can be compared against.");
        }

        var opened = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;

        try
        {
            await dbContext.Database
                .ExecuteSqlRawAsync(TakeQuotaLockSql, [quota.LockKey], cancellationToken)
                .ConfigureAwait(false);

            var current = await quota.CountAsync(cancellationToken).ConfigureAwait(false);
            if (subjectIsCounted ? current > ceiling : current >= ceiling)
            {
                // Refused with nothing written. The transaction ends in the block below, which releases the
                // lock rather than holding it while the caller reports the refusal.
                return StockQuotaAdmission.Refused(limit, current);
            }

            var admission = StockQuotaAdmission.Admitted(
                limit,
                current,
                opened is null ? null : new AdmissionTransaction(opened));

            // Cleared only once the admission holding the transaction exists. A throw from the construction
            // above leaves ownership here, so the block below still ends the transaction.
            opened = null;
            return admission;
        }
        finally
        {
            // Whatever was opened here and not handed over ends here, on a refusal and on a failure.
            // Disposing a transaction that was not committed rolls it back and releases the lock with it.
            if (opened is not null)
            {
                await opened.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    ///     The quota one limit key is admitted against. The count runs under the lock, so anything it needs
    ///     beyond the rows is read there as well rather than before the lock is taken.
    /// </summary>
    /// <param name="key">The limit to admit against.</param>
    /// <returns>The quota.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The key names a limit that is not a count of stored rows.</exception>
    private StockQuota QuotaFor(LicenseLimitKey key)
    {
        return key switch
        {
            LicenseLimitKey.Clients => new StockQuota(
                "propr:quota:clients",
                ct => LicensedResourceCountQueries.CountClientsAsync(dbContext, licensingCapabilityService, ct)),
            LicenseLimitKey.Runners => new StockQuota(
                "propr:quota:runners",
                ct => LicensedResourceCountQueries.CountEnrolledRunnersAsync(
                    dbContext,
                    timeProvider.GetUtcNow(),
                    ct)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(key),
                key,
                "This limit is not a quota counted as stored rows."),
        };
    }

    /// <summary>What one stock quota is serialized on, and the count it is decided against.</summary>
    /// <param name="LockKey">The name the quota's advisory lock is derived from.</param>
    /// <param name="CountAsync">Counts what the installation currently holds for the quota.</param>
    private sealed record StockQuota(string LockKey, Func<CancellationToken, Task<long>> CountAsync);

    /// <summary>The opened transaction, as the admission holds it.</summary>
    private sealed class AdmissionTransaction(IDbContextTransaction transaction) : IStockQuotaAdmissionScope
    {
        public Task CommitAsync(CancellationToken cancellationToken = default) =>
            transaction.CommitAsync(cancellationToken);

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}

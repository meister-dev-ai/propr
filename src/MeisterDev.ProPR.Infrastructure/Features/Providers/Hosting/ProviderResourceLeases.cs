// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Hosting;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

/// <summary>
///     Arbitrates one named resource across the connections of one provider add-in, across the installation.
/// </summary>
/// <remarks>
///     <para>
///         The resource this exists for is a listener port. A port is held by one process and one socket at a
///         time, so two connections of one family cannot both bind it; nothing arbitrated that while the host
///         owned the only listener there was. A second connection that starts an authorization is told which
///         connection holds the port, instead of failing on the bind with an operating-system error naming
///         nothing an operator can act on, or succeeding on a host where the first listener has just closed and
///         delivering a live authorization code to the wrong connection.
///     </para>
///     <para>
///         Recorded rather than held in memory, so the arbitration covers the deployment and not one replica.
///         Every lease expires whether or not it is released, so a replica that died holding one does not hold
///         it for good.
///     </para>
/// </remarks>
/// <param name="binding">The connection and family this handle was given to.</param>
/// <param name="contextFactory">Opens a context per call, independent of whatever built this.</param>
/// <param name="timeProvider">Supplies the instant expiries are judged against, and drives the wait.</param>
public sealed class ProviderResourceLeases(
    ProviderAddInBinding binding,
    IDbContextFactory<MeisterProPRDbContext> contextFactory,
    TimeProvider timeProvider) : IProviderLeases
{
    /// <summary>How long a caller sleeps between attempts while waiting for a resource.</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(250);

    /// <inheritdoc />
    public async Task<ProviderLeaseOutcome> AcquireAsync(
        string resourceName,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var resource = AcceptedName(resourceName);
        var wait = timeout > ProviderHostLimits.MaximumLeaseWait ? ProviderHostLimits.MaximumLeaseWait : timeout;
        var deadline = timeProvider.GetUtcNow() + wait;

        // An attempt refused by the unique index reads who holds the resource on a second context, and the
        // holder can release between the two. The outcome that produces says the resource is taken and names
        // nobody, which is not a state a caller can report. It also means the resource is free, so the attempt
        // is made again at once. Once only: a resource another caller keeps taking would otherwise spin here
        // for a caller who asked not to wait at all.
        var retriedUnheld = false;

        while (true)
        {
            // Each attempt is bounded on its own. The advisory lock it takes is held for the length of another
            // attempt's transaction, so an attempt that runs past the bound is a stalled database, and left
            // unbounded it would hold a caller far past the wait it asked for. The bound is the attempt timeout
            // rather than what is left of the caller's wait: the remainder can be nothing, and cancelling at
            // zero would refuse every caller without reading the row.
            using var bound = new CancellationTokenSource(ProviderHostLimits.LeaseAttemptTimeout, timeProvider);
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct, bound.Token);

            ProviderLeaseOutcome outcome;
            try
            {
                outcome = await this.TryAcquireAsync(resource, attempt.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (bound.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Reported rather than returned as a refusal: a refusal names the connection holding the
                // resource, and this attempt never read who that is. The shared classifier treats a host timeout
                // as transient, so the caller's own retry is the remedy.
                throw new ProviderHostTimeoutException(
                    $"Asking for the resource '{resource}' took longer than "
                    + $"{ProviderHostLimits.LeaseAttemptTimeout.TotalSeconds:0} seconds, so it was given up on.");
            }

            if (outcome.Acquired)
            {
                return outcome;
            }

            if (outcome.HeldByConnection is null && !retriedUnheld)
            {
                retriedUnheld = true;
                continue;
            }

            // What is left of the caller's own timeout, not a whole interval: a caller that asked to wait a
            // hundred milliseconds waits a hundred, rather than the first full interval that follows them.
            var remaining = deadline - timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return outcome;
            }

            await Task.Delay(remaining < RetryInterval ? remaining : RetryInterval, timeProvider, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Gives a held resource back, or does nothing when the lease has already lapsed.</summary>
    /// <remarks>
    ///     The identifier is what makes releasing safe. A lease that lapsed on its expiry may already have been
    ///     taken by another connection, and a release keyed on the resource name alone would take that one away
    ///     from its holder.
    /// </remarks>
    /// <param name="leaseId">The lease as it was granted.</param>
    /// <param name="ct">Cancels the release.</param>
    internal async Task ReleaseAsync(Guid leaseId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.ProviderResourceLeases
            .Where(lease => lease.Id == leaseId)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }

    private async Task<ProviderLeaseOutcome> TryAcquireAsync(string resource, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        // The unique index decides who holds one resource, and it says nothing about how many resources a family
        // holds altogether. That cap is counted and then written, so without serializing the family's attempts
        // every concurrent caller reads the same count, every one is under the limit, and every one inserts.
        await ProviderAdvisoryLock.TakeAsync(db, ProviderAdvisoryLock.ResourceLeases, binding.AddInKey, ct)
            .ConfigureAwait(false);

        // Read after the lock. Taken before it, the wait for the lock was spent against a timestamp that had
        // already gone stale, so a lease that expired during the wait was still counted as held.
        var now = timeProvider.GetUtcNow();

        // A lapsed lease is cleared before a new one is attempted, so the expiry is what makes the resource
        // available again rather than something a later caller has to interpret.
        await db.ProviderResourceLeases
            .Where(lease => lease.AddInKey == binding.AddInKey && lease.ExpiresAt <= now)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        var held = await db.ProviderResourceLeases
            .CountAsync(lease => lease.AddInKey == binding.AddInKey, ct)
            .ConfigureAwait(false);

        if (held >= ProviderHostLimits.MaximumLeasesPerAddIn)
        {
            throw new ProviderRequestRejectedException(
                $"The provider family '{binding.AddInKey}' already holds {held} resources, which is the most the "
                + "host arbitrates for one family at a time.",
                nameof(resource));
        }

        var granted = new ProviderResourceLeaseRecord
        {
            Id = Guid.NewGuid(),
            AddInKey = binding.AddInKey,
            ResourceName = resource,
            ConnectionProfileId = binding.ConnectionProfileId,
            AcquiredAt = now,
            ExpiresAt = now + ProviderHostLimits.MaximumLeaseLifetime,
        };

        db.ProviderResourceLeases.Add(granted);

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The unique index is the arbitration itself. Reading the table and then inserting would let two
            // callers both find the resource free and both insert, and whichever wrote second would take it from
            // the first; here the database refuses the second and the caller is told who holds it. The failed
            // insert poisoned the transaction, so the holder is read on a context of its own.
            db.ChangeTracker.Clear();
            await transaction.RollbackAsync(ct).ConfigureAwait(false);

            await using var reading = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            return new ProviderLeaseOutcome(
                null,
                await this.HolderOfAsync(reading, resource, ct).ConfigureAwait(false));
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return new ProviderLeaseOutcome(new ProviderNamedLease(this, granted.Id, resource), null);
    }

    private async Task<string?> HolderOfAsync(MeisterProPRDbContext db, string resource, CancellationToken ct)
    {
        // Read through the connection rather than stored on the lease, so the name an operator is given is the
        // one the connection carries now and not the one it carried when the resource was taken. Null when the
        // holder released it between the refused insert and this read, which is a loser with nobody left to name
        // rather than a state to report as an error.
        return await db.ProviderResourceLeases
            .AsNoTracking()
            .Where(lease => lease.AddInKey == binding.AddInKey && lease.ResourceName == resource)
            .Join(
                db.AiConnectionProfiles.AsNoTracking(),
                lease => lease.ConnectionProfileId,
                profile => profile.Id,
                (_, profile) => profile.DisplayName)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    private static string AcceptedName(string resourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        return resourceName.Length <= ProviderHostLimits.MaximumResourceNameLength
            ? resourceName
            : throw new ProviderRequestRejectedException(
                $"The resource name is {resourceName.Length} characters; the host arbitrates names of at most "
                + $"{ProviderHostLimits.MaximumResourceNameLength}.",
                nameof(resourceName));
    }

    /// <summary>A held resource, given back by disposing it.</summary>
    /// <param name="owner">The handle that granted it.</param>
    /// <param name="leaseId">The lease as it was recorded.</param>
    /// <param name="resourceName">The resource held.</param>
    private sealed class ProviderNamedLease(ProviderResourceLeases owner, Guid leaseId, string resourceName)
        : IProviderNamedLease
    {
        private bool _released;

        /// <inheritdoc />
        public string ResourceName => resourceName;

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (this._released)
            {
                return;
            }

            // Marked released only once the row is gone. Set before the delete, a transient database failure
            // left the flag true and the row behind, and a later disposal returned without retrying, so the
            // resource stayed held until the lease expired.
            await owner.ReleaseAsync(leaseId, CancellationToken.None).ConfigureAwait(false);
            this._released = true;
        }
    }
}

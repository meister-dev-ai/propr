// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using System.Security.Cryptography;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MeisterDev.ProPR.Infrastructure.Features.Licensing.Persistence;

/// <summary>EF Core-backed store for the identifier an installation reports itself under.</summary>
/// <param name="dbContext">The database context.</param>
/// <param name="timeProvider">Supplies the instant a new identifier is created at.</param>
/// <param name="profileObserver">
///     Captures the installation's system profile when the identifier is created, which is the one moment the
///     baseline can be taken from. It is optional so a host without the profile slice still mints identifiers,
///     and it reports its own failures rather than throwing, so a profile that cannot be captured does not stop
///     the identifier being created.
/// </param>
public sealed class LicensingIdentityRepository(
    MeisterProPRDbContext dbContext,
    TimeProvider timeProvider,
    ISystemProfileObserver? profileObserver = null)
    : ILicensingIdentityStore
{
    private const int SingletonIdentityId = 1;

    /// <inheritdoc />
    public async Task<Guid> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        // The row is read before it is seeded. Every installation past its first read has one, and this runs on
        // the summary the unauthenticated sign-in options are built from, so the steady state is a read rather
        // than a write attempt per caller.
        if (await this.TryReadAsync(cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return existing.Identifier;
        }

        await this.EnsureSeededAsync(cancellationToken).ConfigureAwait(false);

        var seeded = await this.TryReadAsync(cancellationToken).ConfigureAwait(false)
                     ?? throw new InvalidOperationException(
                         "The licensing identity row was not present after seeding it. Check that the database schema is up to date.");

        if (profileObserver is not null)
        {
            await profileObserver.ObserveAsync(seeded.CreatedAt, cancellationToken).ConfigureAwait(false);
        }

        return seeded.Identifier;
    }

    /// <inheritdoc />
    public async Task<DateTimeOffset?> GetCreatedAtAsync(CancellationToken cancellationToken = default)
    {
        return (await this.TryReadAsync(cancellationToken).ConfigureAwait(false))?.CreatedAt;
    }

    private async Task<LicensingIdentityRecord?> TryReadAsync(CancellationToken cancellationToken)
    {
        return await dbContext.LicensingIdentity
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == SingletonIdentityId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Produces an identifier from 16 bytes of the cryptographic random generator, so all 128 bits are
    ///     random.
    ///     <para>
    ///         <see cref="Guid.NewGuid" /> is not used because it fixes the version and variant bits, leaving
    ///         122 random ones and giving every value it produces the same recognisable shape. This value is
    ///         quoted in reports and support conversations, so it is generated with nothing fixed in it.
    ///     </para>
    /// </summary>
    private static Guid NewIdentifier()
    {
        return new Guid(RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>
    ///     Creates the singleton row if the installation has none.
    ///     <para>
    ///         Two replicas reading for the first time together each propose an identifier. Both can pass the
    ///         check below, so the loser of the insert is told the key is taken and reads the row the winner
    ///         wrote: an installation ends up with one identifier rather than reporting itself as two.
    ///     </para>
    /// </summary>
    private async Task EnsureSeededAsync(CancellationToken cancellationToken)
    {
        if (await dbContext.LicensingIdentity
                .AnyAsync(row => row.Id == SingletonIdentityId, cancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }

        var entry = dbContext.LicensingIdentity.Add(
            new LicensingIdentityRecord
            {
                Id = SingletonIdentityId,
                Identifier = NewIdentifier(),
                CreatedAt = timeProvider.GetUtcNow(),
            });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsAlreadySeededViolation(exception))
        {
            // Another replica created the row first, and its identifier is the installation's. The caller
            // reads the row back, so nothing else is needed here.
        }
        finally
        {
            // The row is read back through a query that tracks nothing, so the proposed entry has no further
            // use. Left tracked, it would collide on the key with a later seed on the same context.
            entry.State = EntityState.Detached;
        }
    }

    private static bool IsAlreadySeededViolation(DbUpdateException exception)
    {
        return exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
    }
}

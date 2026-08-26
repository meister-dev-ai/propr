// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using System.Security.Cryptography;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.Licensing.Persistence;

/// <summary>EF Core-backed store for the license document an installation has activated.</summary>
public sealed class InstallationLicenseRepository(
    MeisterProPRDbContext dbContext,
    ISecretProtectionCodec secretProtectionCodec,
    TimeProvider timeProvider) : IActivatedLicenseStore
{
    /// <summary>
    ///     The codec purpose for the stored license document. It sits beside the connection-secret and
    ///     archive purposes; a value protected under one purpose cannot be read back under another.
    /// </summary>
    private const string LicenseTokenPurpose = "installation-license-token";

    private const int SingletonLicenseId = 1;

    // This lock covers the installation's singleton document. It is transaction-scoped, so a cancelled or failed
    // mutation releases it with the transaction and another replica can proceed.
    private const long LicenseMutationLockKey = 0x4D50_524C_4943_4E53;

    private const string PostgresProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <inheritdoc />
    public async Task<StoredLicense?> GetAsync(CancellationToken cancellationToken = default)
    {
        var record = await dbContext.InstallationLicenses
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == SingletonLicenseId, cancellationToken)
            .ConfigureAwait(false);

        if (record is null)
        {
            return null;
        }

        return new StoredLicense
        {
            CompactLicense = this.TryUnprotect(record.ProtectedToken),
            ActivatedAt = record.ActivatedAt,
            ActivatedByUserId = record.ActivatedByUserId,
        };
    }

    /// <inheritdoc />
    public async Task<LicenseMutation> ReplaceAsync(
        string compactLicense,
        Guid? activatedByUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(compactLicense);

        var protectedToken = secretProtectionCodec.Protect(compactLicense, LicenseTokenPurpose);
        var activatedAt = timeProvider.GetUtcNow();

        return this.IsPostgres()
            ? await this.ReplacePostgresAsync(protectedToken, activatedAt, activatedByUserId, cancellationToken)
                .ConfigureAwait(false)
            : await this.ReplaceWithoutUpsertAsync(protectedToken, activatedAt, activatedByUserId, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetAsync(
        string compactLicense,
        Guid? activatedByUserId,
        CancellationToken cancellationToken = default)
    {
        await this.ReplaceAsync(compactLicense, activatedByUserId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<LicenseMutation> RemoveAndGetAsync(CancellationToken cancellationToken = default)
    {
        return this.IsPostgres()
            ? await this.RemovePostgresAsync(cancellationToken).ConfigureAwait(false)
            : await this.RemoveWithoutBulkDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(CancellationToken cancellationToken = default)
    {
        await this.RemoveAndGetAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Reads the stored value back, or reports that it could not be read.
    ///     <para>
    ///         A value the installation's data-protection keys cannot open is returned as absent rather than
    ///         raised, because the caller has a state for it and a license that cannot be read is not a fault
    ///         in the read path.
    ///     </para>
    ///     <para>
    ///         Absent here means only that: a value carrying no codec envelope passes through the codec
    ///         verbatim and reaches the verifier, which reports it as invalid, or as verified if someone wrote
    ///         a signed document into the column in the clear. The unreadable outcome is reserved
    ///         for a value whose envelope the installation's key ring cannot open.
    ///     </para>
    /// </summary>
    private string? TryUnprotect(string storedValue)
    {
        try
        {
            var compactLicense = secretProtectionCodec.Unprotect(storedValue, LicenseTokenPurpose);

            return string.IsNullOrWhiteSpace(compactLicense) ? null : compactLicense;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private bool IsPostgres() =>
        string.Equals(dbContext.Database.ProviderName, PostgresProviderName, StringComparison.Ordinal);

    /// <summary>
    ///     Replaces the singleton row while holding the installation mutation lock. The lock has to cover the
    ///     prior read as well as the write, because the handler's history action derives from that prior row.
    /// </summary>
    private async Task<LicenseMutation> ReplacePostgresAsync(
        string protectedToken,
        DateTimeOffset activatedAt,
        Guid? activatedByUserId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await this.AcquireMutationLockAsync(cancellationToken).ConfigureAwait(false);
        var previous = await this.GetAsync(cancellationToken).ConfigureAwait(false);
        var mutatedAt = await this.ReadMutationInstantAsync(cancellationToken).ConfigureAwait(false);

        // Whether the row exists is decided by the read above, which ran under the mutation lock. Nothing else
        // can insert or remove the row between that read and this write, so the insert cannot conflict and the
        // update cannot miss.
        if (previous is null)
        {
            var entry = dbContext.InstallationLicenses.Add(
                new InstallationLicenseRecord
                {
                    Id = SingletonLicenseId,
                    ProtectedToken = protectedToken,
                    ActivatedAt = activatedAt,
                    ActivatedByUserId = activatedByUserId,
                });

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            // The row is read back through a query that tracks nothing. Left tracked, the entry would collide
            // on the key with a later activation on the same context that follows a removal.
            entry.State = EntityState.Detached;
        }
        else
        {
            await dbContext.InstallationLicenses
                .Where(row => row.Id == SingletonLicenseId)
                .ExecuteUpdateAsync(
                    row => row
                        .SetProperty(record => record.ProtectedToken, protectedToken)
                        .SetProperty(record => record.ActivatedAt, activatedAt)
                        .SetProperty(record => record.ActivatedByUserId, activatedByUserId),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new LicenseMutation(previous, mutatedAt);
    }

    /// <summary>Removes the singleton row while holding the same lock replacements use.</summary>
    private async Task<LicenseMutation> RemovePostgresAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await this.AcquireMutationLockAsync(cancellationToken).ConfigureAwait(false);
        var removed = await this.GetAsync(cancellationToken).ConfigureAwait(false);
        var mutatedAt = await this.ReadMutationInstantAsync(cancellationToken).ConfigureAwait(false);

        if (removed is not null)
        {
            await dbContext.InstallationLicenses
                .Where(row => row.Id == SingletonLicenseId)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new LicenseMutation(removed, mutatedAt);
    }

    /// <summary>
    ///     Reads the instant this mutation is ordered by, from the database rather than from the host clock.
    /// </summary>
    /// <remarks>
    ///     Mutations of the singleton row are serialized by the lock this transaction holds, so the instants
    ///     the database reports for them run in the order the mutations did. A replica stamping its own clock
    ///     would order them by the skew between replicas instead, and the history could then show a removal as
    ///     the newest action on an installation that another replica had already re-licensed.
    ///     <para>
    ///         Read as the statement runs rather than as the transaction started. A transaction's start instant
    ///         is fixed at its first statement, which is before the lock is acquired, so a transaction that
    ///         began earlier and waited longer for the lock would carry the earlier instant and the history
    ///         would order it ahead of the mutation that superseded it.
    ///     </para>
    /// </remarks>
    private async Task<DateTimeOffset> ReadMutationInstantAsync(CancellationToken cancellationToken)
    {
        var instants = await dbContext.Database
            .SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"")
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return instants[0];
    }

    private Task<int> AcquireMutationLockAsync(CancellationToken cancellationToken)
    {
        return dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({LicenseMutationLockKey})",
            cancellationToken);
    }

    /// <summary>
    ///     Removes the singleton row on providers without a bulk delete, which is the in-memory test host.
    /// </summary>
    private async Task<LicenseMutation> RemoveWithoutBulkDeleteAsync(CancellationToken cancellationToken)
    {
        var record = await dbContext.InstallationLicenses
            .SingleOrDefaultAsync(row => row.Id == SingletonLicenseId, cancellationToken)
            .ConfigureAwait(false);

        if (record is null)
        {
            return new LicenseMutation(null, timeProvider.GetUtcNow());
        }

        var removed = ToStoredLicense(record);
        dbContext.InstallationLicenses.Remove(record);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new LicenseMutation(removed, timeProvider.GetUtcNow());
    }

    /// <summary>Writes the singleton row on providers without an upsert, which is the in-memory test host.</summary>
    private async Task<LicenseMutation> ReplaceWithoutUpsertAsync(
        string protectedToken,
        DateTimeOffset activatedAt,
        Guid? activatedByUserId,
        CancellationToken cancellationToken)
    {
        var record = await dbContext.InstallationLicenses
            .SingleOrDefaultAsync(row => row.Id == SingletonLicenseId, cancellationToken)
            .ConfigureAwait(false);

        var previous = record is null ? null : ToStoredLicense(record);

        if (record is null)
        {
            record = new InstallationLicenseRecord { Id = SingletonLicenseId };
            dbContext.InstallationLicenses.Add(record);
        }

        record.ProtectedToken = protectedToken;
        record.ActivatedAt = activatedAt;
        record.ActivatedByUserId = activatedByUserId;

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new LicenseMutation(previous, timeProvider.GetUtcNow());
    }

    private StoredLicense ToStoredLicense(InstallationLicenseRecord record) =>
        new()
        {
            CompactLicense = this.TryUnprotect(record.ProtectedToken),
            ActivatedAt = record.ActivatedAt,
            ActivatedByUserId = record.ActivatedByUserId,
        };
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MeisterDev.ProPR.Infrastructure.Features.Licensing.Persistence;

/// <summary>EF Core-backed store for the highest instant the installation has observed.</summary>
public sealed class InstallationObservedTimeRepository(MeisterProPRDbContext dbContext) : IHighestObservedTimeStore
{
    private const int SingletonObservedTimeId = 1;
    private const string PostgresProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <inheritdoc />
    public async Task<DateTimeOffset?> GetAsync(CancellationToken cancellationToken = default)
    {
        var record = await dbContext.InstallationObservedTime
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == SingletonObservedTimeId, cancellationToken)
            .ConfigureAwait(false);

        return record?.ObservedAt;
    }

    /// <inheritdoc />
    public async Task<DateTimeOffset> AdvanceToAsync(DateTimeOffset instant, CancellationToken cancellationToken = default)
    {
        if (!this.IsPostgres())
        {
            await this.AdvanceWithoutBulkUpdateAsync(instant, cancellationToken).ConfigureAwait(false);

            return await this.ReadAfterAdvanceAsync(instant, cancellationToken).ConfigureAwait(false);
        }

        // The condition on the row is the monotonicity rule: a row already at or above the instant matches
        // nothing, so the database is what decides which of the two values survives. A replica that read the
        // value, compared it in memory and wrote back could lower the row, because another replica may have
        // raised it in between.
        await this.RaiseAsync(instant, cancellationToken).ConfigureAwait(false);

        var recorded = await this.GetAsync(cancellationToken).ConfigureAwait(false);

        if (recorded is null)
        {
            // No row was updated and none is present, so this is the installation's first observation. An
            // update cannot create the row, so the cold path inserts it.
            await this.CreateAsync(instant, cancellationToken).ConfigureAwait(false);
            recorded = await this.GetAsync(cancellationToken).ConfigureAwait(false);
        }

        return AtLeast(recorded, instant);
    }

    /// <summary>
    ///     Raises the row to the instant, leaving a row that already holds an instant at or above it untouched.
    /// </summary>
    private Task<int> RaiseAsync(DateTimeOffset instant, CancellationToken cancellationToken)
    {
        return dbContext.InstallationObservedTime
            .Where(row => row.Id == SingletonObservedTimeId && row.ObservedAt < instant)
            .ExecuteUpdateAsync(row => row.SetProperty(record => record.ObservedAt, instant), cancellationToken);
    }

    /// <summary>Creates the row for the installation's first observation.</summary>
    /// <remarks>
    ///     Replicas starting together can all reach this point, and only one of them creates the row. The
    ///     others are told the key is taken, and re-apply the raise rather than treating the write as done: the
    ///     row that was created may hold an earlier instant than the one they carry.
    /// </remarks>
    private async Task CreateAsync(DateTimeOffset instant, CancellationToken cancellationToken)
    {
        var entry = dbContext.InstallationObservedTime.Add(
            new InstallationObservedTimeRecord
            {
                Id = SingletonObservedTimeId,
                ObservedAt = instant,
            });

        var created = true;

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsAlreadyCreatedViolation(exception))
        {
            created = false;
        }
        finally
        {
            // The row is read back through a query that tracks nothing, so the proposed entry has no further
            // use. Left tracked, it would collide on the key with a later write on the same context.
            entry.State = EntityState.Detached;
        }

        if (!created)
        {
            await this.RaiseAsync(instant, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsAlreadyCreatedViolation(DbUpdateException exception)
    {
        return exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
    }

    private bool IsPostgres() =>
        string.Equals(dbContext.Database.ProviderName, PostgresProviderName, StringComparison.Ordinal);

    /// <summary>
    ///     Reads the row back after a write that has already succeeded, so the caller decides from what the
    ///     installation holds rather than from what this replica supplied.
    /// </summary>
    /// <remarks>
    ///     The read is separate from the write, so another replica can raise the row in between. The value
    ///     that produces is still one the row held and is at least the supplied instant, because the stored
    ///     value only rises. A row that is missing after a write that reported success would mean it was
    ///     deleted in between; the supplied instant is returned in that case, which is the value the write put
    ///     there.
    /// </remarks>
    private async Task<DateTimeOffset> ReadAfterAdvanceAsync(DateTimeOffset instant, CancellationToken cancellationToken)
    {
        return AtLeast(await this.GetAsync(cancellationToken).ConfigureAwait(false), instant);
    }

    /// <summary>
    ///     The higher of the value read back and the value this replica supplied, which is what keeps the
    ///     returned instant from falling below the write that has already succeeded.
    /// </summary>
    private static DateTimeOffset AtLeast(DateTimeOffset? recorded, DateTimeOffset instant)
    {
        return recorded is { } value && value > instant ? value : instant;
    }

    /// <summary>
    ///     Advances the singleton row on providers without a bulk update, which is the in-memory test host. The
    ///     comparison is made in memory here, so this path holds for one writer at a time; concurrent writers
    ///     are what the condition on the row above covers.
    /// </summary>
    private async Task AdvanceWithoutBulkUpdateAsync(DateTimeOffset instant, CancellationToken cancellationToken)
    {
        var record = await dbContext.InstallationObservedTime
            .SingleOrDefaultAsync(row => row.Id == SingletonObservedTimeId, cancellationToken)
            .ConfigureAwait(false);

        if (record is null)
        {
            dbContext.InstallationObservedTime.Add(
                new InstallationObservedTimeRecord
                {
                    Id = SingletonObservedTimeId,
                    ObservedAt = instant,
                });

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (record.ObservedAt >= instant)
        {
            return;
        }

        record.ObservedAt = instant;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace MeisterDev.ProPR.Infrastructure.Features.Licensing.Persistence;

/// <summary>
///     Store for the months whose counted authors went above the number the license states.
/// </summary>
public sealed class AuthorOverageRepository(MeisterProPRDbContext dbContext) : IAuthorOverageStore
{
    private const string PostgresProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <summary>
    ///     How far the last-observed instant may fall behind before an observation that changes nothing else
    ///     moves it.
    ///     <para>
    ///         The value mirrors the licensing clock's advance interval, which keeps the same kind of write on
    ///         the same granularity. It is not configurable: the instant reports when the month was last seen
    ///         above the number, and nothing reads it to a finer granularity than that.
    ///     </para>
    /// </summary>
    internal static readonly TimeSpan ObservationInterval = TimeSpan.FromHours(1);

    /// <summary>
    ///     The first observation of a month. The conflict clause keeps the row that is there, so replicas
    ///     evaluating the same month at the same moment cannot both insert and exactly one of them is told it
    ///     wrote the row.
    ///     <para>
    ///         Separate from the update that follows because a single upsert reports one affected row whether it
    ///         inserted or updated, and the report of a month entering overage is taken from which of the two
    ///         happened.
    ///     </para>
    /// </summary>
    private const string InsertSql = """
                                     INSERT INTO licensing_author_overage
                                         (overage_month, licensed_count, highest_observed_count,
                                          first_observed_at, last_observed_at)
                                     VALUES (
                                         @overage_month,
                                         @licensed_count,
                                         @observed_count,
                                         @observed_at,
                                         @observed_at)
                                     ON CONFLICT (overage_month) DO NOTHING
                                     """;

    /// <inheritdoc />
    public Task<bool> RecordAsync(
        DateOnly month,
        long licensedCount,
        long observedCount,
        CancellationToken cancellationToken = default)
    {
        return this.RecordAsync(month, licensedCount, observedCount, observedAt: null, cancellationToken);
    }

    /// <summary>
    ///     Records the month against a stated instant rather than the database clock.
    /// </summary>
    /// <remarks>
    ///     The instant is what the first-observed and last-observed columns hold and what the interval between
    ///     two observations is measured on. Stating it is how that ordering is exercised, because the database
    ///     clock cannot be moved from outside the database; the evaluation path states none.
    /// </remarks>
    /// <param name="month">The first day of the UTC month the count covers.</param>
    /// <param name="licensedCount">The number the license states for authors within one calendar month.</param>
    /// <param name="observedCount">The distinct authors the month holds.</param>
    /// <param name="observedAt">The instant to attribute the observation to, or null for the database clock.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether this write was the month's first observation above the number.</returns>
    internal async Task<bool> RecordAsync(
        DateOnly month,
        long licensedCount,
        long observedCount,
        DateTimeOffset? observedAt,
        CancellationToken cancellationToken = default)
    {
        if (month.Day != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(month), month, "The month must be the first day of the month.");
        }

        if (!this.IsPostgres())
        {
            return await this
                .RecordWithoutUpsertAsync(month, licensedCount, observedCount, observedAt, cancellationToken)
                .ConfigureAwait(false);
        }

        // One instant covers both statements, so the row they write and the row they then update carry
        // consistent first-observed and last-observed values. Resolving an unstated instant costs a round
        // trip; the database clock is what supplies it, so replicas whose own clocks disagree order their
        // observations of a month against one clock.
        var observed = observedAt?.ToUniversalTime()
                       ?? await this.ReadDatabaseInstantAsync(cancellationToken).ConfigureAwait(false);

        var inserted = await dbContext.Database.ExecuteSqlRawAsync(
                InsertSql,
                [
                    new NpgsqlParameter("overage_month", NpgsqlDbType.Date) { Value = month },
                    new NpgsqlParameter("observed_at", NpgsqlDbType.TimestampTz) { Value = observed },
                    new NpgsqlParameter("licensed_count", licensedCount),
                    new NpgsqlParameter("observed_count", observedCount),
                ],
                cancellationToken)
            .ConfigureAwait(false);

        if (inserted == 1)
        {
            return true;
        }

        // A later observation of a month that already has a row. Both values only rise, so an evaluation that
        // reads a lower count than an earlier one leaves the highest count as it was and moves the
        // last-observed instant alone.
        //
        // The condition on the row is what keeps a repeat observation from writing anything. A month above the
        // number is observed on every lifecycle sweep and every administration read, and without the condition
        // each of those would write a row version and take a row lock to store the values the row already
        // holds. With it, only an observation that raises the highest count, or one an interval after the
        // last, writes.
        var lastObservedBefore = observed - ObservationInterval;

        await dbContext.LicensingAuthorOverage
            .Where(row => row.OverageMonth == month
                          && (row.HighestObservedCount < observedCount
                              || row.LastObservedAt <= lastObservedBefore))
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(
                        record => record.HighestObservedCount,
                        record => record.HighestObservedCount > observedCount
                            ? record.HighestObservedCount
                            : observedCount)
                    .SetProperty(
                        record => record.LastObservedAt,
                        record => record.LastObservedAt > observed ? record.LastObservedAt : observed),
                cancellationToken)
            .ConfigureAwait(false);

        return false;
    }

    /// <summary>
    ///     Reads the instant the observation is attributed to from the database rather than from the host clock.
    /// </summary>
    /// <remarks>
    ///     The month an evaluation falls in is decided by this value, and replicas of one installation read
    ///     their own host clocks. Taking the instant from the database is what puts their evaluations in one
    ///     month whatever their own clocks read.
    /// </remarks>
    private async Task<DateTimeOffset> ReadDatabaseInstantAsync(CancellationToken cancellationToken)
    {
        var instants = await dbContext.Database
            .SqlQueryRaw<DateTimeOffset>("SELECT now() AS \"Value\"")
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return instants[0];
    }

    /// <summary>
    ///     Records the month on a provider without an upsert, which is the in-memory test host. There is no
    ///     database clock to read there, so an unstated instant falls back to the process clock.
    /// </summary>
    /// <remarks>
    ///     The ratchet the update statement performs is done here after the read instead, including the
    ///     condition that keeps a repeat observation from writing. The host has one writer, so the read and the
    ///     write cannot interleave with another.
    /// </remarks>
    private async Task<bool> RecordWithoutUpsertAsync(
        DateOnly month,
        long licensedCount,
        long observedCount,
        DateTimeOffset? observedAt,
        CancellationToken cancellationToken)
    {
        var observed = observedAt ?? DateTimeOffset.UtcNow;

        var existing = await dbContext.LicensingAuthorOverage
            .FirstOrDefaultAsync(row => row.OverageMonth == month, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            dbContext.LicensingAuthorOverage.Add(
                new LicensingAuthorOverageRecord
                {
                    OverageMonth = month,
                    LicensedCount = licensedCount,
                    HighestObservedCount = observedCount,
                    FirstObservedAt = observed,
                    LastObservedAt = observed,
                });

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return true;
        }

        var raisesTheCount = observedCount > existing.HighestObservedCount;
        var movesTheInstant = observed - existing.LastObservedAt >= ObservationInterval;

        if (!raisesTheCount && !movesTheInstant)
        {
            return false;
        }

        existing.HighestObservedCount = Math.Max(existing.HighestObservedCount, observedCount);
        existing.LastObservedAt = observed > existing.LastObservedAt ? observed : existing.LastObservedAt;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return false;
    }

    private bool IsPostgres() =>
        string.Equals(dbContext.Database.ProviderName, PostgresProviderName, StringComparison.Ordinal);
}

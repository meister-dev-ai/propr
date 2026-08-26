// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace MeisterDev.ProPR.Infrastructure.Features.Licensing.Persistence;

/// <summary>
///     Store for the month-and-author rollup, and the two reads over it.
/// </summary>
public sealed class AuthorActivityRollupRepository(MeisterProPRDbContext dbContext) : IAuthorActivityRollupStore
{
    /// <summary>The current month and the eleven before it.</summary>
    private const int TrailingMonths = 12;

    private const string PostgresProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <summary>
    ///     One statement covers the first observation of an author in a month and every later one. The conflict
    ///     clause keeps the row that is there and raises its exclusion flag when this observation was of
    ///     automation, so replicas completing work for the same author at the same moment cannot both insert.
    ///     <para>
    ///         The flag is raised and never lowered: the update sets it true, and it runs only where the row
    ///         holds false and this observation was of automation. An account seen as automation on any
    ///         observation in the month is excluded for that month, and a later observation of it as a person
    ///         leaves the flag raised, because the earlier signal is not undone by the account also asking a
    ///         question.
    ///     </para>
    ///     <para>
    ///         Nothing else is updated. The month and the first-seen instant come from the same source, the
    ///         instant the caller stated or the database clock when it stated none, and the conflict clause
    ///         leaves both at what the first observation recorded. Reading the clock in the statement is what
    ///         puts every replica's completion in one month whatever their own clocks read.
    ///     </para>
    ///     <para>
    ///         The condition on the update is what keeps a repeat observation from writing anything. Without it
    ///         every completion for an author the month already holds would write a row version and take a row
    ///         lock to store the value the row has. With it, only the observation that raises the flag writes.
    ///     </para>
    /// </summary>
    private const string UpsertSql = """
                                     INSERT INTO licensing_author_activity
                                         (activity_month, author_key, excluded, provider, host_base_url,
                                          external_user_id, first_seen_at, first_seen_source)
                                     VALUES (
                                         date_trunc('month', COALESCE(@observed_at, now()) AT TIME ZONE 'UTC')::date,
                                         @author_key,
                                         @excluded,
                                         @provider,
                                         @host_base_url,
                                         @external_user_id,
                                         COALESCE(@observed_at, now()),
                                         @first_seen_source)
                                     ON CONFLICT (activity_month, author_key) DO UPDATE
                                         SET excluded = true
                                         WHERE NOT licensing_author_activity.excluded AND EXCLUDED.excluded
                                     """;

    /// <inheritdoc />
    public Task RecordAuthorAsync(
        ProviderHostRef host,
        string externalUserId,
        AuthorActivitySource source,
        bool excluded,
        CancellationToken cancellationToken = default)
    {
        return this.RecordAuthorAsync(host, externalUserId, source, excluded, observedAt: null, cancellationToken);
    }

    /// <summary>
    ///     Records the author against a stated instant rather than the database clock.
    /// </summary>
    /// <remarks>
    ///     The month a completion falls in changes at midnight UTC, and the clock the statement reads cannot be
    ///     moved to that boundary from outside the database. Stating the instant is how the boundary is
    ///     exercised; the completion paths state none.
    /// </remarks>
    /// <param name="host">The host that issued the identifier.</param>
    /// <param name="externalUserId">The author's identifier as that host issues it.</param>
    /// <param name="source">Which kind of finished work made the observation.</param>
    /// <param name="excluded">Whether this observation identified the author as automation.</param>
    /// <param name="observedAt">The instant to attribute the observation to, or null for the database clock.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the month holds the author.</returns>
    internal async Task RecordAuthorAsync(
        ProviderHostRef host,
        string externalUserId,
        AuthorActivitySource source,
        bool excluded,
        DateTimeOffset? observedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalUserId);

        var identifier = externalUserId.Trim();
        var authorKey = host.ScopedKey(identifier);

        if (!this.IsPostgres())
        {
            await this.RecordWithoutUpsertAsync(
                    host,
                    identifier,
                    authorKey,
                    source,
                    excluded,
                    observedAt,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await dbContext.Database.ExecuteSqlRawAsync(
                UpsertSql,
                [
                    // Converted to UTC before it is sent. A stated instant carries whatever offset its caller
                    // had, and the month is a UTC one, so the offset is resolved here rather than left to be
                    // read as if it were UTC.
                    new NpgsqlParameter("observed_at", NpgsqlDbType.TimestampTz)
                    {
                        Value = observedAt?.ToUniversalTime() ?? (object)DBNull.Value,
                    },
                    new NpgsqlParameter("author_key", authorKey),
                    new NpgsqlParameter("excluded", excluded),
                    new NpgsqlParameter("provider", (int)host.Provider),
                    new NpgsqlParameter("host_base_url", host.HostBaseUrl),
                    new NpgsqlParameter("external_user_id", identifier),
                    new NpgsqlParameter("first_seen_source", (int)source),
                ],
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> CountCurrentMonthAuthorsAsync(CancellationToken cancellationToken = default)
    {
        var month = await this.ReadCurrentMonthAsync(cancellationToken).ConfigureAwait(false);

        return await dbContext.LicensingAuthorActivity
            .AsNoTracking()
            .CountAsync(row => row.ActivityMonth == month && !row.Excluded, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> CountCurrentMonthExcludedAuthorsAsync(CancellationToken cancellationToken = default)
    {
        var month = await this.ReadCurrentMonthAsync(cancellationToken).ConfigureAwait(false);

        return await dbContext.LicensingAuthorActivity
            .AsNoTracking()
            .CountAsync(row => row.ActivityMonth == month && row.Excluded, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AuthorActivityMonthCounts> GetCurrentMonthCountsAsync(CancellationToken cancellationToken = default)
    {
        var month = await this.ReadCurrentMonthAsync(cancellationToken).ConfigureAwait(false);
        var counts = await dbContext.LicensingAuthorActivity
            .AsNoTracking()
            .Where(row => row.ActivityMonth == month)
            .GroupBy(_ => 1)
            .Select(rows => new
            {
                Counted = rows.LongCount(row => !row.Excluded),
                Excluded = rows.LongCount(row => row.Excluded),
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return new AuthorActivityMonthCounts(month, counts?.Counted ?? 0, counts?.Excluded ?? 0);
    }

    /// <inheritdoc />
    public async Task<AuthorMonthCount?> GetTrailingYearPeakAsync(CancellationToken cancellationToken = default)
    {
        var currentMonth = await this.ReadCurrentMonthAsync(cancellationToken).ConfigureAwait(false);
        var windowStart = currentMonth.AddMonths(-(TrailingMonths - 1));

        // Ordered by count and then by the later month, so two months holding the same peak report the more
        // recent one. The count is what a licensed period is measured against; the month names when it happened.
        // Counted as a 64-bit value, which is the width the database returns a count in.
        var peak = await dbContext.LicensingAuthorActivity
            .AsNoTracking()
            .Where(row => !row.Excluded && row.ActivityMonth >= windowStart && row.ActivityMonth <= currentMonth)
            .GroupBy(row => row.ActivityMonth)
            .Select(months => new { Month = months.Key, AuthorCount = months.LongCount() })
            .OrderByDescending(month => month.AuthorCount)
            .ThenByDescending(month => month.Month)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return peak is null ? null : new AuthorMonthCount(peak.Month, peak.AuthorCount);
    }

    /// <summary>
    ///     The first day of the month the database's own clock is in, in UTC.
    /// </summary>
    /// <remarks>
    ///     Read from the database rather than the process, so the month a read reports is the month the writes
    ///     landed in. Converted through UTC explicitly, because <c>date_trunc</c> over the session's time zone
    ///     would report the adjacent month for the hours either side of midnight UTC.
    /// </remarks>
    private async Task<DateOnly> ReadCurrentMonthAsync(CancellationToken cancellationToken)
    {
        if (!this.IsPostgres())
        {
            return CurrentMonthFrom(DateTimeOffset.UtcNow);
        }

        var months = await dbContext.Database
            .SqlQuery<DateOnly>($"""SELECT date_trunc('month', now() AT TIME ZONE 'UTC')::date AS "Value" """)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return months[0];
    }

    /// <summary>
    ///     Records the author on a provider without an upsert, which is the in-memory test host. There is no
    ///     database clock to read there, so an unstated instant falls back to the process clock.
    /// </summary>
    /// <remarks>
    ///     The escalation the conflict clause performs is done here in two statements instead. The host has one
    ///     writer, so the read and the write cannot interleave with another.
    /// </remarks>
    private async Task RecordWithoutUpsertAsync(
        ProviderHostRef host,
        string externalUserId,
        string authorKey,
        AuthorActivitySource source,
        bool excluded,
        DateTimeOffset? observedAt,
        CancellationToken cancellationToken)
    {
        var firstSeenAt = observedAt ?? DateTimeOffset.UtcNow;
        var month = CurrentMonthFrom(firstSeenAt);

        var existing = await dbContext.LicensingAuthorActivity
            .FirstOrDefaultAsync(row => row.ActivityMonth == month && row.AuthorKey == authorKey, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            if (!excluded || existing.Excluded)
            {
                return;
            }

            existing.Excluded = true;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return;
        }

        dbContext.LicensingAuthorActivity.Add(
            new LicensingAuthorActivityRecord
            {
                ActivityMonth = month,
                AuthorKey = authorKey,
                Excluded = excluded,
                Provider = host.Provider,
                HostBaseUrl = host.HostBaseUrl,
                ExternalUserId = externalUserId,
                FirstSeenAt = firstSeenAt,
                FirstSeenSource = source,
            });

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DateOnly CurrentMonthFrom(DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();

        return new DateOnly(utc.Year, utc.Month, 1);
    }

    private bool IsPostgres() =>
        string.Equals(dbContext.Database.ProviderName, PostgresProviderName, StringComparison.Ordinal);
}

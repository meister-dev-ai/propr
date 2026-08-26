// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.Licensing.Persistence;

/// <summary>Store for the highest number of reviews seen executing at the same time on a UTC day.</summary>
public sealed class ConcurrentReviewPeakRepository(MeisterProPRDbContext dbContext) : IConcurrentReviewPeakStore
{
    private const string PostgresProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <summary>
    ///     Counts the executing reviews and ratchets the day's row in one statement.
    ///     <para>
    ///         The day and the count both come from the database, so replicas whose host clocks disagree
    ///         record against one day and observe one state. The condition on the conflict clause is what
    ///         keeps the statement from writing a row version on every claim: once the day's recorded peak is
    ///         at or above the observed count, the update matches nothing.
    ///     </para>
    /// </summary>
    private const string ObserveSql = """
                                      INSERT INTO licensing_concurrent_review_peak (peak_date, peak_count, observed_at)
                                      SELECT (now() AT TIME ZONE 'UTC')::date, count(*), now()
                                        FROM review_jobs
                                       WHERE status = 'Processing'
                                      ON CONFLICT (peak_date) DO UPDATE
                                         SET peak_count = EXCLUDED.peak_count,
                                             observed_at = EXCLUDED.observed_at
                                       WHERE licensing_concurrent_review_peak.peak_count < EXCLUDED.peak_count
                                      """;

    /// <inheritdoc />
    public async Task ObserveAsync(CancellationToken cancellationToken = default)
    {
        if (!this.IsPostgres())
        {
            await this.ObserveWithoutUpsertAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await dbContext.Database.ExecuteSqlRawAsync(ObserveSql, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<long?> GetPreviousDayPeakAsync(CancellationToken cancellationToken = default)
    {
        var day = await this.ResolvePreviousDayAsync(cancellationToken).ConfigureAwait(false);

        var record = await dbContext.LicensingConcurrentReviewPeak
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.PeakDate == day, cancellationToken)
            .ConfigureAwait(false);

        return record?.PeakCount;
    }

    /// <summary>
    ///     Resolves the UTC day before the current one from the database clock, which is the clock the rows
    ///     were keyed by, so a host clock a day out does not read the wrong row.
    /// </summary>
    private async Task<DateOnly> ResolvePreviousDayAsync(CancellationToken cancellationToken)
    {
        if (!this.IsPostgres())
        {
            return DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime).AddDays(-1);
        }

        var days = await dbContext.Database
            .SqlQueryRaw<DateOnly>("SELECT ((now() AT TIME ZONE 'UTC')::date - 1) AS \"Value\"")
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return days[0];
    }

    private bool IsPostgres() =>
        string.Equals(dbContext.Database.ProviderName, PostgresProviderName, StringComparison.Ordinal);

    /// <summary>
    ///     Records the observation on providers without an upsert, which is the in-memory test host. The count,
    ///     the read and the write are separate steps here, so this path holds for one writer at a time.
    /// </summary>
    private async Task ObserveWithoutUpsertAsync(CancellationToken cancellationToken)
    {
        var observedAt = DateTimeOffset.UtcNow;
        var day = DateOnly.FromDateTime(observedAt.UtcDateTime);

        var executing = await dbContext.ReviewJobs
            .CountAsync(job => job.Status == Domain.Enums.JobStatus.Processing, cancellationToken)
            .ConfigureAwait(false);

        var record = await dbContext.LicensingConcurrentReviewPeak
            .SingleOrDefaultAsync(row => row.PeakDate == day, cancellationToken)
            .ConfigureAwait(false);

        if (record is null)
        {
            dbContext.LicensingConcurrentReviewPeak.Add(
                new LicensingConcurrentReviewPeakRecord
                {
                    PeakDate = day,
                    PeakCount = executing,
                    ObservedAt = observedAt,
                });

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (record.PeakCount >= executing)
        {
            return;
        }

        record.PeakCount = executing;
        record.ObservedAt = observedAt;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
